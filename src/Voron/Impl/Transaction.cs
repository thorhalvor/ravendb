﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Voron.Data;
using Voron.Data.BTrees;
using Voron.Data.Compact;
using Voron.Data.Fixed;
using Voron.Debugging;
using Voron.Impl.FileHeaders;

namespace Voron.Impl
{
    public unsafe class Transaction : IDisposable
    {
        private Dictionary<Tuple<Tree, Slice>, Tree> _multiValueTrees;
        private readonly LowLevelTransaction _lowLevelTransaction;

        public LowLevelTransaction LowLevelTransaction
        {
            get { return _lowLevelTransaction; }
        }

        internal Action<Transaction> AfterCommit = delegate { };

        private readonly Dictionary<string, Tree> _trees = new Dictionary<string, Tree>();


        public Transaction(LowLevelTransaction lowLevelTransaction)
        {
            _lowLevelTransaction = lowLevelTransaction;
        }


        public Tree ReadTree(string treeName)
        {
            Tree tree;
            if (_trees.TryGetValue(treeName, out tree))
                return tree;

            var header = (TreeRootHeader*)_lowLevelTransaction.RootObjects.DirectRead((Slice)treeName);
            if (header != null)
            {
                if(header->RootObjectType != RootObjectType.VariableSizeTree)
                    throw new InvalidOperationException("Tried to opened " + treeName + " as a variable size tree, but it is actually a " + header->RootObjectType);

                tree = Tree.Open(_lowLevelTransaction, this, header);
                tree.Name = treeName;
                _trees.Add(treeName, tree);
                return tree;
            }

            _trees.Add(treeName, null);
            return null;
        }

      
        public IEnumerable<Tree> Trees
        {
            get { return _trees.Values; }
        }

        public void Commit()
        {
            CommitTrees();
            _lowLevelTransaction.Commit();
        }

        internal void CommitTrees()
        {
            if (_multiValueTrees != null)
            {
                foreach (var multiValueTree in _multiValueTrees)
                {
                    var parentTree = multiValueTree.Key.Item1;
                    var key = multiValueTree.Key.Item2;
                    var childTree = multiValueTree.Value;

                    var trh =
                        (TreeRootHeader*) parentTree.DirectAdd(key, sizeof (TreeRootHeader), TreeNodeFlags.MultiValuePageRef);
                    childTree.State.CopyTo(trh);
                }
            }

            foreach (var tree in Trees)
            {
                if (tree == null)
                    continue;
                tree.State.InWriteTransaction = false;
                var treeState = tree.State;
                if (treeState.IsModified)
                {
                    var treePtr =
                        (TreeRootHeader*) _lowLevelTransaction.RootObjects.DirectAdd((Slice) tree.Name, sizeof (TreeRootHeader));
                    treeState.CopyTo(treePtr);
                }
            }
        }


        internal void AddMultiValueTree(Tree tree, Slice key, Tree mvTree)
        {
            if (_multiValueTrees == null)
                _multiValueTrees = new Dictionary<Tuple<Tree, Slice>, Tree>(new TreeAndSliceComparer());
            mvTree.IsMultiValueTree = true;
            _multiValueTrees.Add(Tuple.Create(tree, key), mvTree);
        }

        internal bool TryGetMultiValueTree(Tree tree, Slice key, out Tree mvTree)
        {
            mvTree = null;
            if (_multiValueTrees == null)
                return false;
            return _multiValueTrees.TryGetValue(Tuple.Create(tree, key), out mvTree);
        }

        internal bool TryRemoveMultiValueTree(Tree parentTree, Slice key)
        {
            var keyToRemove = Tuple.Create(parentTree, key);
            if (_multiValueTrees == null || !_multiValueTrees.ContainsKey(keyToRemove))
                return false;

            return _multiValueTrees.Remove(keyToRemove);
        }


        internal void AddTree(string name, Tree tree)
        {
            Tree value;
            if (_trees.TryGetValue(name, out value) && value != null)
            {
                throw new InvalidOperationException("Tree already exists: " + name);
            }
            _trees[name] = tree;
        }



        public void DeleteTree(string name)
        {
            if (_lowLevelTransaction.Flags == (TransactionFlags.ReadWrite) == false)
                throw new ArgumentException("Cannot create a new newRootTree with a read only transaction");

            Tree tree = ReadTree(name);
            if (tree == null)
                return;

            foreach (var page in tree.AllPages())
            {
                _lowLevelTransaction.FreePage(page);
            }

            _lowLevelTransaction.RootObjects.Delete((Slice)name);

            _trees.Remove(name);
        }

        public void RenameTree(string fromName, string toName)
        {
            if (_lowLevelTransaction.Flags == (TransactionFlags.ReadWrite) == false)
                throw new ArgumentException("Cannot rename a new tree with a read only transaction");

            if (toName.Equals(Constants.RootTreeName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cannot create a tree with reserved name: " + toName);

            if (ReadTree(toName) != null)
                throw new ArgumentException("Cannot rename a tree with the name of an existing tree: " + toName);

            Tree fromTree = ReadTree(fromName);
            if (fromTree == null)
                throw new ArgumentException("Tree " + fromName + " does not exists");

            Slice key = (Slice)toName;

            _lowLevelTransaction.RootObjects.Delete((Slice)fromName);
            var ptr = _lowLevelTransaction.RootObjects.DirectAdd(key, sizeof(TreeRootHeader));
            fromTree.State.CopyTo((TreeRootHeader*)ptr);
            fromTree.Name = toName;
            fromTree.State.IsModified = true;

            _trees.Remove(fromName);
            _trees.Remove(toName);

            AddTree(toName, fromTree);
        }

        public Tree CreateTree(string name)
        {
            Tree tree = ReadTree(name);
            if (tree != null)
                return tree;

            if (_lowLevelTransaction.Flags == (TransactionFlags.ReadWrite) == false)
                throw new InvalidOperationException("No such tree: '" + name + "' and cannot create trees in read transactions");

            Slice key = name;

            tree = Tree.Create(_lowLevelTransaction, this);
            tree.Name = name;
            var space = _lowLevelTransaction.RootObjects.DirectAdd(key, sizeof(TreeRootHeader));

            tree.State.CopyTo((TreeRootHeader*)space);
            tree.State.IsModified = true;
            AddTree(name, tree);

            return tree;
        }


        public void Dispose()
        {
            _lowLevelTransaction?.Dispose();
        }

        public FixedSizeTree FixedTreeFor(Slice treeName)
        {
            var valueSize = FixedSizeTree.GetValueSize(LowLevelTransaction, LowLevelTransaction.RootObjects,
                treeName);
            return FixedTreeFor(treeName, valueSize);
        }


        public FixedSizeTree FixedTreeFor(Slice treeName, ushort valSize)
        {
            return new FixedSizeTree(LowLevelTransaction, LowLevelTransaction.RootObjects, treeName, valSize);
        }

        public PrefixTree PrefixTreeFor(Slice treeName)
        {
            return PrefixTree.Open(LowLevelTransaction, LowLevelTransaction.RootObjects, treeName);
        }


        public RootObjectType GetRootObjectType(Slice name)
        {
            var val = _lowLevelTransaction.RootObjects.DirectRead(name);
            if (val == null)
                return RootObjectType.None;

            return ((RootHeader*) val)->RootObjectType;
        }
    }

    public static class TransactionLegacyExtensions
    {
        public static TreePage GetReadOnlyTreePage(this LowLevelTransaction tx, long pageNumber)
        {
            return tx.GetPage(pageNumber).ToTreePage();
        }

        public static FixedSizeTreePage GetReadOnlyFixedSizeTreePage(this LowLevelTransaction tx, long pageNumber)
        {
            return tx.GetPage(pageNumber).ToFixedSizeTreePage();
        }
    }
}