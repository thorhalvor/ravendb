//-----------------------------------------------------------------------
// <copyright file="Tasks.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Isam.Esent.Interop;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Logging;
using Raven.Database.Tasks;

namespace Raven.Database.Storage.Esent.StorageActions
{
    public partial class DocumentStorageActions : ITasksStorageActions
    {
        private static int bookmarkMost = SystemParameters.BookmarkMost;

        public void AddTask(DatabaseTask task, DateTime addedAt)
        {
            int actualBookmarkSize;
            var bookmark = new byte[bookmarkMost];
            using (var update = new Update(session, Tasks, JET_prep.Insert))
            {
                Api.SetColumn(session, Tasks, tableColumnsCache.TasksColumns["task"], task.AsBytes());
                Api.SetColumn(session, Tasks, tableColumnsCache.TasksColumns["for_index"], task.Index);
                Api.SetColumn(session, Tasks, tableColumnsCache.TasksColumns["task_type"], task.GetType().FullName, Encoding.Unicode);
                Api.SetColumn(session, Tasks, tableColumnsCache.TasksColumns["added_at"], addedAt.ToBinary());

                update.Save(bookmark, bookmark.Length, out actualBookmarkSize);
            }
            Api.JetGotoBookmark(session, Tasks, bookmark, actualBookmarkSize);
        }

        public bool HasTasks
        {
            get
            {
                return Api.TryMoveFirst(session, Tasks);
            }
        }

        public long ApproximateTaskCount
        {
            get
            {
                if (Api.TryMoveFirst(session, Tasks) == false)
                    return 0;
                var first = (int)Api.RetrieveColumnAsInt32(session, Tasks, tableColumnsCache.TasksColumns["id"]);
                if (Api.TryMoveLast(session, Tasks) == false)
                    return 0;
                var last = (int)Api.RetrieveColumnAsInt32(session, Tasks, tableColumnsCache.TasksColumns["id"]);

                var result = last - first;
                return result == 0 ? 1 : result;
            }
        }

        public T GetMergedTask<T>(List<int> indexesToSkip, int[] allIndexes)
            where T : DatabaseTask
        {
            Api.MoveBeforeFirst(session, Tasks);

            while (Api.TryMoveNext(session, Tasks))
            {
                var taskType = Api.RetrieveColumnAsString(session, Tasks, tableColumnsCache.TasksColumns["task_type"], Encoding.Unicode);
                if (taskType != typeof(T).FullName)
                    continue;

                var taskAsBytes = Api.RetrieveColumn(session, Tasks, tableColumnsCache.TasksColumns["task"]);
                DatabaseTask task;
                try
                {
                    task = DatabaseTask.ToTask(taskType, taskAsBytes);
                }
                catch (Exception e)
                {
                    logger.ErrorException(
                        string.Format("Could not create instance of a task: {0}", taskAsBytes),
                        e);

                    DeleteTask();
                    continue;
                }

                if (indexesToSkip.Contains(task.Index))
                {
                    if (logger.IsDebugEnabled)
                        logger.Debug("Skipping task for index id: {0}", task.Index);
                    continue;
                }

                if (allIndexes.Contains(task.Index) == false)
                {
                    if (logger.IsDebugEnabled)
                        logger.Debug("Deleting task for non existing index id: {0}", task.Index);

                    DeleteTask();
                    continue;
                }

                var currentId = Api.RetrieveColumnAsInt32(session, Tasks, tableColumnsCache.TasksColumns["id"]).Value;

                try
                {
                    Api.JetDelete(session, Tasks);
                }
                catch (EsentErrorException e)
                {
                    logger.WarnException(string.Format("Failed to delete task id: {0}, for index id {1}", 
                        currentId, task.Index), e);

                    if (e.Error == JET_err.WriteConflict)
                        continue;
                    throw;
                }

                if (logger.IsDebugEnabled)
                    logger.Debug("Fetched task id: {0}", currentId);

                task.Id = currentId;
                MergeSimilarTasks(task);

                return (T)task;
            }

            return null;
        }

        private void DeleteTask()
        {
            try
            {
                Api.JetDelete(session, Tasks);
            }
            catch (EsentErrorException e)
            {
                logger.WarnException("Failed to delete task", e);

                if (e.Error == JET_err.WriteConflict)
                    throw new ConcurrencyException("Failed to delete task");
                throw;
            }
        }

        public IEnumerable<TaskMetadata> GetPendingTasksForDebug()
        {
            Api.MoveBeforeFirst(session, Tasks);
            while (Api.TryMoveNext(session, Tasks))
            {
                var type = Api.RetrieveColumnAsString(session, Tasks, tableColumnsCache.TasksColumns["task_type"], Encoding.Unicode);
                var index = Api.RetrieveColumnAsInt32(session, Tasks, tableColumnsCache.TasksColumns["for_index"]);
                var addedTime64 = Api.RetrieveColumnAsInt64(session, Tasks, tableColumnsCache.TasksColumns["added_at"]).Value;
                var id = Api.RetrieveColumnAsInt32(session, Tasks, tableColumnsCache.TasksColumns["id"]).Value;

                yield return new TaskMetadata
                {
                    Id = id,
                    AddedTime = DateTime.FromBinary(addedTime64),
                    IndexId = index ?? -1,
                    Type = type
                };
            }
        }

        private void MergeSimilarTasks(DatabaseTask task)
        {
            var expectedTaskType = task.GetType().FullName;

            Api.JetSetCurrentIndex(session, Tasks, "by_index_and_task_type");

            if (task.SeparateTasksByIndex)
            {
                Api.MakeKey(session, Tasks, task.Index, MakeKeyGrbit.NewKey);
                Api.MakeKey(session, Tasks, expectedTaskType, Encoding.Unicode, MakeKeyGrbit.None);
                // there are no tasks matching the current one, just return
                if (Api.TrySeek(session, Tasks, SeekGrbit.SeekEQ) == false)
                {
                    return;
                }
                Api.MakeKey(session, Tasks, task.Index, MakeKeyGrbit.NewKey);
                Api.MakeKey(session, Tasks, expectedTaskType, Encoding.Unicode, MakeKeyGrbit.None);
                Api.JetSetIndexRange(session, Tasks, SetIndexRangeGrbit.RangeInclusive | SetIndexRangeGrbit.RangeUpperLimit);
            }
            else
            {
                if (Api.TryMoveFirst(session, Tasks) == false)
                    return;
            }

            var totalKeysToProcess = task.NumberOfKeys;
            do
            {
                if (totalKeysToProcess >= 5 * 1024)
                    break;

                // esent index ranges are approximate, and we need to check them ourselves as well
                if (Api.RetrieveColumnAsString(session, Tasks, tableColumnsCache.TasksColumns["task_type"]) != expectedTaskType)
                    continue;

                int currentId = -1;
                DatabaseTask existingTask;
                try
                {
                    var taskAsBytes = Api.RetrieveColumn(session, Tasks, tableColumnsCache.TasksColumns["task"]);
                    var taskType = Api.RetrieveColumnAsString(session, Tasks, tableColumnsCache.TasksColumns["task_type"], Encoding.Unicode);
                    try
                    {
                        existingTask = DatabaseTask.ToTask(taskType, taskAsBytes);
                    }
                    catch (Exception e)
                    {
                        logger.ErrorException(
                            string.Format("Could not create instance of a task: {0}", taskAsBytes),
                            e);

                        DeleteTask();
                        continue;
                    }

                    currentId = Api.RetrieveColumnAsInt32(session, Tasks, tableColumnsCache.TasksColumns["id"]).Value;
                    totalKeysToProcess += existingTask.NumberOfKeys;
                    Api.JetDelete(session, Tasks);
                }
                catch (EsentErrorException e)
                {
                    logger.WarnException(string.Format("Failed to merge task id: {0}, for index id: {1}", 
                        currentId, task.Index), e);

                    if (e.Error == JET_err.WriteConflict)
                        return;

                    throw;
                }

                task.Merge(existingTask);
                if (logger.IsDebugEnabled)
                    logger.Debug("Merged task id: {0} with task id: {1}", currentId, task.Id);

            } while (Api.TryMoveNext(session, Tasks));
        }
    }
}
