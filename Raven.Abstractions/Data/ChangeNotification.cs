﻿// -----------------------------------------------------------------------
//  <copyright file="ChangeNotification.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using Raven.Json.Linq;

namespace Raven.Abstractions.Data
{
	public class DocumentChangeNotification : EventArgs
	{
		public DocumentChangeTypes Type { get; set; }
		public string Id { get; set; }
		public Etag Etag { get; set; }

		public override string ToString()
		{
			return string.Format("{0} on {1}", Type, Id);
		}
	}

	[Flags]
	public enum DocumentChangeTypes
	{
		None = 0,

		Put = 1,
		Delete = 2,
		BulkInsertStarted = 4,
		BulkInsertEnded = 8,

		Common = Put | Delete
	}

	[Flags]
	public enum IndexChangeTypes
	{
		None = 0,

		MapCompleted = 1,
		ReduceCompleted = 2,
		RemoveFromIndex = 4,

		IndexAdded = 8,
		IndexRemoved = 16,

        IndexDemotedToIdle = 32,
        IndexPromotedFromIdle = 64,

		IndexDemotedToAbandoned = 128,
	}

	public class IndexChangeNotification : EventArgs
	{
		public IndexChangeTypes Type { get; set; }
		public string Name { get; set; }
		public Etag Etag { get; set; }

		public override string ToString()
		{
			return string.Format("{0} on {1}", Type, Name);
		}
	}

	public class ReplicationConflictNotification : EventArgs
	{
		public ReplicationConflictTypes Type { get; set; }
		public string Id { get; set; }
		public RavenJObject[] ConflictedDocs { get; set; }

		public override string ToString()
		{
			return string.Format("{0} on {1}", Type, Id);
		}
	}

	[Flags]
	public enum ReplicationConflictTypes
	{
		None = 0,

		DocumentReplicationConflict = 1,
		AttachmentReplicationConflict = 2
	}
}