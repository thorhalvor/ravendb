﻿using System;
using System.Collections.Generic;
using System.Linq;
using Sparrow.Json;

namespace Raven.Client.Documents.Blit
{
    public static class BlittableExtentions
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="self"></param>
        /// <param name="path"></param>
        /// <param name="createSnapshots">Set to true if you want to modify selected objects</param>
        /// <returns></returns>
        public static IEnumerable<Tuple<object, object>> SelectTokenWithRavenSyntaxReturningFlatStructure(this BlittableJsonReaderBase self, string path, bool createSnapshots = false)
        {
            var pathParts = path.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            object result = null;
            result = new BlitPath(pathParts[0]).Evaluate(self, false);

            if (pathParts.Length == 1)
            {
                yield return Tuple.Create(result, (object)self);
                yield break;
            }

            if (result is BlittableJsonReaderObject)
            {
                var blitResult = result as BlittableJsonReaderObject;
                blitResult.TryGetMember("$values", out result);

                for (var i = 0; i < blitResult.Count; i++)
                {
                    var item = blitResult.GetPropertyByIndex(i);

                    if (item.Item2 is BlittableJsonReaderBase)
                    {
                        var itemAsBlittable = item.Item2 as BlittableJsonReaderBase;
                        foreach (var subItem in itemAsBlittable.SelectTokenWithRavenSyntaxReturningFlatStructure(string.Join(",", pathParts.Skip(1).ToArray())))
                        {
                            yield return subItem;
                        }
                    }
                    else
                    {
                        yield return Tuple.Create((object)item, result);
                    }
                }
            }
            else if (result is BlittableJsonReaderArray)
            {
                var blitResult = result as BlittableJsonReaderArray;
                for (var i = 0; i < blitResult.Length; i++)
                {
                    var item = blitResult[i];

                    if (item is BlittableJsonReaderBase)
                    {
                        var itemAsBlittable = item as BlittableJsonReaderBase;
                        foreach (var subItem in itemAsBlittable.SelectTokenWithRavenSyntaxReturningFlatStructure(string.Join(",", pathParts.Skip(1).ToArray())))
                        {
                            yield return subItem;
                        }
                    }
                    else
                    {
                        yield return Tuple.Create((object)item, result);
                    }
                }
            }
            else
            {
                throw new ArgumentException("Illegal path");
            }
        }
    }
}