using Sparrow;
// -----------------------------------------------------------------------
//  <copyright file="Tree.MultiTree.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Diagnostics;
using System.IO;
using Voron.Impl;
using Voron.Impl.FileHeaders;
using Voron.Impl.Paging;
using Voron.Util;

namespace Voron.Trees
{
    /* Multi tree behavior
     * -------------------
     * A multi tree is a tree that is used only with MultiRead, MultiAdd, MultiDelete
     * The common use case is a secondary index that allows duplicates. 
     * 
     * The API exposed goes like this:
     * 
     * MultiAdd("key", "val1"), MultiAdd("key", "val2"), MultiAdd("key", "val3") 
     * 
     * And then you can read it back with MultiRead("key") : IIterator
     * 
     * When deleting, you delete one value at a time: MultiDelete("key", "val1")
     * 
     * The actual values are stored as keys in a separate tree per key. In order to optimize
     * space usage, multi trees work in the following fashion.
     * 
     * If the totale size of the values per key is less than NodeMaxSize, we store them as an embedded
     * page inside the owning tree. If then are more than the node max size, we create a separate tree
     * for them and then only store the tree root infromation.
     */
    public unsafe partial class Tree
    {
        public bool IsMultiValueTree { get; set; }

        public void MultiAdd(Slice key, Slice value, ushort? version = null)
        {
            if (value == null) throw new ArgumentNullException("value");
            int maxNodeSize = Tx.DataPager.NodeMaxSize;
            if (value.Size > maxNodeSize)
                throw new ArgumentException(
                    "Cannot add a value to child tree that is over " + maxNodeSize + " bytes in size", "value");
            if (value.Size == 0)
                throw new ArgumentException("Cannot add empty value to child tree");

            State.IsModified = true;
            State.Flags |= TreeFlags.MultiValueTrees;

            Lazy<TreeCursor> lazy;
            TreeNodeHeader* node;
            var page = FindPageFor(key, out node, out lazy);
            if ((page == null || page.LastMatch != 0))
            {
                MultiAddOnNewValue(_tx, key, value, version, maxNodeSize);
                return;
            }

            page = _tx.ModifyPage(page.PageNumber, this, page);
            var item = page.GetNode(page.LastSearchPosition);

            // already was turned into a multi tree, not much to do here
            if (item->Flags == TreeNodeFlags.MultiValuePageRef)
            {
                var existingTree = OpenMultiValueTree(_tx, key, item);
                existingTree.DirectAdd(value, 0, version: version);
                return;
            }

            byte* nestedPagePtr;
            if (item->Flags == TreeNodeFlags.PageRef)
            {
                var overFlowPage = _tx.ModifyPage(item->PageNumber, this ,null);
                nestedPagePtr = overFlowPage.Base + Constants.PageHeaderSize;
            }
            else
            {
                nestedPagePtr = TreeNodeHeader.DirectAccess(_tx, item);
            }

            var nestedPage = new TreePage(nestedPagePtr, "multi tree", (ushort)TreeNodeHeader.GetDataSize(_tx, item));

            var existingItem = nestedPage.Search(value);
            if (nestedPage.LastMatch != 0)
                existingItem = null;// not an actual match, just greater than

            ushort previousNodeRevision = existingItem != null ?  existingItem->Version : (ushort)0;
            CheckConcurrency(key, value, version, previousNodeRevision, TreeActionType.Add);
            
            if (existingItem != null)
            {
                // maybe same value added twice?
                var tmpKey = page.GetNodeKey(item);
                if (tmpKey.Compare(value) == 0)
                    return; // already there, turning into a no-op
                nestedPage.RemoveNode(nestedPage.LastSearchPosition);
            }

            var valueToInsert = value;

            if (nestedPage.HasSpaceFor(_tx, valueToInsert, 0))
            {
                // we are now working on top of the modified root page, we can just modify the memory directly
                nestedPage.AddDataNode(nestedPage.LastSearchPosition, valueToInsert, 0, previousNodeRevision);
                return;
            }

            int pageSize = nestedPage.CalcSizeUsed() + Constants.PageHeaderSize;
            var newRequiredSize = pageSize + nestedPage.GetRequiredSpace(valueToInsert, 0);
            if (newRequiredSize <= maxNodeSize)
            {
                // we can just expand the current value... no need to create a nested tree yet
                var actualPageSize = (ushort) Math.Min(Utils.NearestPowerOfTwo(newRequiredSize), maxNodeSize);

                var currentDataSize = TreeNodeHeader.GetDataSize(_tx, item);
                ExpandMultiTreeNestedPageSize(_tx, key, value, nestedPagePtr, actualPageSize, currentDataSize);

                return;
            }
            // we now have to convert this into a tree instance, instead of just a nested page
            var tree = Create(_tx, TreeFlags.MultiValue);
            for (int i = 0; i < nestedPage.NumberOfEntries; i++)
            {
                var existingValue = nestedPage.GetNodeKey(i);
                tree.DirectAdd(existingValue, 0);
            }
            tree.DirectAdd(value, 0, version: version);
            _tx.AddMultiValueTree(this, key, tree);
            // we need to record that we switched to tree mode here, so the next call wouldn't also try to create the tree again
            DirectAdd(key, sizeof (TreeRootHeader), TreeNodeFlags.MultiValuePageRef);
        }

        private void ExpandMultiTreeNestedPageSize(Transaction tx, Slice key, Slice value, byte* nestedPagePtr, ushort newSize, int currentSize)
        {
            Debug.Assert(newSize > currentSize);
            TemporaryPage tmp;
            using (tx.Environment.GetTemporaryPage(tx, out tmp))
            {
                var tempPagePointer = tmp.TempPagePointer;
                Memory.Copy(tempPagePointer, nestedPagePtr, currentSize);
                Delete(key); // release our current page
                TreePage nestedPage = new TreePage(tempPagePointer, "multi tree", (ushort)currentSize);

                var ptr = DirectAdd(key, newSize);

                var newNestedPage = new TreePage(ptr, "multi tree", newSize)
                {
                    Lower = (ushort)Constants.PageHeaderSize,
                    Upper = newSize,
                    Flags = TreePageFlags.Leaf,
                    PageNumber = -1L // mark as invalid page number
                };
            
                Slice nodeKey = nestedPage.CreateNewEmptyKey();
                for (int i = 0; i < nestedPage.NumberOfEntries; i++)
                {
                    var nodeHeader = nestedPage.GetNode(i);
                    nestedPage.SetNodeKey(nodeHeader, ref nodeKey);
                    newNestedPage.AddDataNode(i, nodeKey, 0, (ushort)(nodeHeader->Version - 1)); // we dec by one because AdddataNode will inc by one, and we don't want to change those values
                }

                newNestedPage.Search(value);
                newNestedPage.AddDataNode(newNestedPage.LastSearchPosition, value, 0, 0);
            }
        }

        private void MultiAddOnNewValue(Transaction tx, Slice key, Slice value, ushort? version, int maxNodeSize)
        {
            Slice valueToInsert = value;

            var requiredPageSize = Constants.PageHeaderSize + SizeOf.LeafEntry(-1, valueToInsert, 0) + Constants.NodeOffsetSize;
            if (requiredPageSize > maxNodeSize)
            {
                // no choice, very big value, we might as well just put it in its own tree from the get go...
                // otherwise, we would have to put this in overflow page, and that won't save us any space anyway

                var tree = Create(tx, TreeFlags.MultiValue);
                tree.DirectAdd(value, 0);
                tx.AddMultiValueTree(this, key, tree);

                DirectAdd(key, sizeof (TreeRootHeader), TreeNodeFlags.MultiValuePageRef);
                return;
            }

            var actualPageSize = (ushort) Math.Min(Utils.NearestPowerOfTwo(requiredPageSize), maxNodeSize);

            var ptr = DirectAdd(key, actualPageSize);

            var nestedPage = new TreePage(ptr, "multi tree", actualPageSize)
            {
                PageNumber = -1L,// hint that this is an inner page
                Lower = (ushort) Constants.PageHeaderSize,
                Upper = actualPageSize,
                Flags = TreePageFlags.Leaf,
            };

            CheckConcurrency(key, value, version, 0, TreeActionType.Add);

            nestedPage.AddDataNode(0, valueToInsert, 0, 0);
        }

        public void MultiDelete(Slice key, Slice value, ushort? version = null)
        {
            State.IsModified = true;
            Lazy<TreeCursor> lazy;
            TreeNodeHeader* node;
            var page = FindPageFor(key, out node, out lazy);
            if (page == null || page.LastMatch != 0)
            {
                return; //nothing to delete - key not found
            }

            page = _tx.ModifyPage(page.PageNumber, this, page);

            var item = page.GetNode(page.LastSearchPosition);

            if (item->Flags == TreeNodeFlags.MultiValuePageRef) //multi-value tree exists
            {
                var tree = OpenMultiValueTree(_tx, key, item);

                tree.Delete(value, version);

                // previously, we would convert back to a simple model if we dropped to a single entry
                // however, it doesn't really make sense, once you got enough values to go to an actual nested 
                // tree, you are probably going to remain that way, or be removed completely.
                if (tree.State.EntriesCount != 0) 
                    return;
                _tx.TryRemoveMultiValueTree(this, key);
                _tx.FreePage(tree.State.RootPageNumber);

                Delete(key);
            }
            else // we use a nested page here
            {
                var nestedPage = new TreePage(TreeNodeHeader.DirectAccess(_tx, item), "multi tree", (ushort)TreeNodeHeader.GetDataSize(_tx, item));
                var nestedItem = nestedPage.Search(value);
                if (nestedPage.LastMatch != 0) // value not found
                    return;

                byte* nestedPagePtr;
                if (item->Flags == TreeNodeFlags.PageRef)
                {
                    var overFlowPage = _tx.ModifyPage(item->PageNumber, this, null);
                    nestedPagePtr = overFlowPage.Base + Constants.PageHeaderSize;
                }
                else
                {
                    nestedPagePtr = TreeNodeHeader.DirectAccess(_tx, item);
                }

                nestedPage = new TreePage(nestedPagePtr, "multi tree", (ushort)TreeNodeHeader.GetDataSize(_tx, item))
                {
                    LastSearchPosition = nestedPage.LastSearchPosition
                };

                CheckConcurrency(key, value, version, nestedItem->Version, TreeActionType.Delete);
                nestedPage.RemoveNode(nestedPage.LastSearchPosition);
                if (nestedPage.NumberOfEntries == 0)
                    Delete(key);
            }
        }

        //TODO: write a test for this
        public long MultiCount(Slice key)
        {
            Lazy<TreeCursor> lazy;
            TreeNodeHeader* node;
            var page = FindPageFor(key, out node, out lazy);
            if (page == null || page.LastMatch != 0)
                return 0;

            Debug.Assert(node != null);

            var fetchedNodeKey = page.GetNodeKey(node);
            if (fetchedNodeKey.Compare(key) != 0)
            {
                throw new InvalidDataException("Was unable to retrieve the correct node. Data corruption possible");
            }

            if (node->Flags == TreeNodeFlags.MultiValuePageRef)
            {
                var tree = OpenMultiValueTree(_tx, key, node);

                return tree.State.EntriesCount;
            }

            var nestedPage = new TreePage(TreeNodeHeader.DirectAccess(_tx, node), "multi tree", (ushort)TreeNodeHeader.GetDataSize(_tx, node));

            return nestedPage.NumberOfEntries;
        }

        public IIterator MultiRead(Slice key)
        {
            Lazy<TreeCursor> lazy;
            TreeNodeHeader* node;
            var page = FindPageFor(key, out node, out lazy);
            if (page == null || page.LastMatch != 0)
                return new EmptyIterator();

            Debug.Assert(node != null);

            var fetchedNodeKey = page.GetNodeKey(node);
            if (fetchedNodeKey.Compare(key) != 0)
            {
                throw new InvalidDataException("Was unable to retrieve the correct node. Data corruption possible");
            }

            if (node->Flags == TreeNodeFlags.MultiValuePageRef)
            {
                var tree = OpenMultiValueTree(_tx, key, node);

                return tree.Iterate();
            }

            var nestedPage = new TreePage(TreeNodeHeader.DirectAccess(_tx, node), "multi tree", (ushort)TreeNodeHeader.GetDataSize(_tx, node));
                
            return new TreePageIterator(nestedPage);
        }

        private Tree OpenMultiValueTree(Transaction tx, Slice key, TreeNodeHeader* item)
        {
            Tree tree;
            if (tx.TryGetMultiValueTree(this, key, out tree))
                return tree;

            var childTreeHeader =
                (TreeRootHeader*)((byte*)item + item->KeySize + Constants.NodeHeaderSize);

            Debug.Assert(childTreeHeader->RootPageNumber < tx.State.NextPageNumber);
            Debug.Assert(childTreeHeader->Flags == TreeFlags.MultiValue);
            
            tree = Open(tx, childTreeHeader);

            tx.AddMultiValueTree(this, key, tree);
            return tree;
        }

        private bool TryOverwriteDataOrMultiValuePageRefNode(TreeNodeHeader* updatedNode, Slice key, int len,
                                                             TreeNodeFlags requestedNodeType, ushort? version, out byte* pos)
        {
            switch (requestedNodeType)
            {
                case TreeNodeFlags.Data:
                case TreeNodeFlags.MultiValuePageRef:
                    {
                        if (updatedNode->DataSize == len &&
                            (updatedNode->Flags == TreeNodeFlags.Data || updatedNode->Flags == TreeNodeFlags.MultiValuePageRef))
                        {
                            CheckConcurrency(key, version, updatedNode->Version, TreeActionType.Add);

                            if (updatedNode->Version == ushort.MaxValue)
                                updatedNode->Version = 0;
                            updatedNode->Version++;

                            updatedNode->Flags = requestedNodeType;

                            {
                                pos = (byte*)updatedNode + Constants.NodeHeaderSize + updatedNode->KeySize;
                                return true;
                            }
                        }
                        break;
                    }
                case TreeNodeFlags.PageRef:
                    throw new InvalidOperationException("We never add PageRef explicitly");
                default:
                    throw new ArgumentOutOfRangeException();
            }
            pos = null;
            return false;
        }
    }
}