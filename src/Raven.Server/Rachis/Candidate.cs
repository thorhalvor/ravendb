﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Raven.Server.ServerWide.Context;

namespace Raven.Server.Rachis
{
    public class Candidate : IDisposable
    {
        private TaskCompletionSource<object> _stateChange = new TaskCompletionSource<object>();
        private readonly RachisConsensus _engine;
        private readonly List<CandidateAmbassador> _voters = new List<CandidateAmbassador>();
        private readonly ManualResetEvent _peersWaiting = new ManualResetEvent(false);
        private Thread _thread;
        private bool _running;
        public long RunRealElectionAtTerm { get; private set; }

        public bool Running
        {
            get { return Volatile.Read(ref _running); }
            private set { Volatile.Write(ref _running, value); }
        }


        public Candidate(RachisConsensus engine)
        {
            _engine = engine;
        }

        public long ElectionTerm { get; private set; }

        private void Run()
        {
            //TODO: Error handling!

            Running = true;

            TransactionOperationContext context;
            ClusterTopology clusterTopology;
            using (_engine.ContextPool.AllocateOperationContext(out context))
            using (var tx = context.OpenReadTransaction())
            {
                ElectionTerm = _engine.CurrentTerm + 1;

                clusterTopology = _engine.GetTopology(context);

                tx.Commit();
            }

            if (IsForcedElection)
            {
                CastVoteForSelf();
            }

            foreach (var voter in clusterTopology.Voters)
            {
                if (voter == _engine.Url)
                    continue; // we already voted for ourselves
                var candidateAmbassador = new CandidateAmbassador(_engine, this,voter, clusterTopology.ApiKey);
                _voters.Add(candidateAmbassador);
                _engine.AppendStateDisposable(this, candidateAmbassador);
                candidateAmbassador.Start();
            }
            while (Running)
            {
                if (_peersWaiting.WaitOne(_engine.Timeout.TimeoutPeriod) == false)
                {
                    // timeout? 
                    if (IsForcedElection)
                    {
                        CastVoteForSelf();
                    }

                    StateChange(); // will wake ambassadors and make them ping peers again
                    continue;
                }
                if (Running == false)
                    return;

                _peersWaiting.Reset();

                var trialElectionsCount = 1;
                var realElectionsCount = 1;
                foreach (var ambassador in _voters)
                {
                    if (ambassador.ReadlElectionWonAtTerm == ElectionTerm)
                        realElectionsCount++;
                    if (ambassador.TrialElectionWonAtTerm == ElectionTerm)
                        trialElectionsCount++;
                }

                var majority = (_voters.Count/2) + 1;
                if (realElectionsCount >= majority)
                {
                    _engine.SwitchToLeaderState();
                    break;
                }
                if (RunRealElectionAtTerm != ElectionTerm && 
                    trialElectionsCount >= majority)
                {
                    CastVoteForSelf();
                }
            }
        }

        private void CastVoteForSelf()
        {
            TransactionOperationContext context;
            using (_engine.ContextPool.AllocateOperationContext(out context))
            using (var tx = context.OpenWriteTransaction())
            {
                ElectionTerm = _engine.CurrentTerm + 1;

                _engine.CastVoteInTerm(context, ElectionTerm, _engine.Url);

                RunRealElectionAtTerm = ElectionTerm;

                tx.Commit();
            }

            StateChange();
        }

        public bool IsForcedElection { get; set; }

        private void StateChange()
        {
            var tcs = Interlocked.Exchange(ref _stateChange, new TaskCompletionSource<object>());
            tcs.TrySetResult(null);
        }

        public void WaitForChangeInState()
        {
            _stateChange.Task.Wait();
        }

        public void Start()
        {
            _thread = new Thread(Run)
            {
                Name = "Consensus Leader - " + _engine.Url,
                IsBackground = true
            };
            _thread.Start();
        }

        public void Dispose()
        {
            Running = false;
            _stateChange.TrySetCanceled();
            _peersWaiting.Set();
            //TODO: shutdown notification of some kind?
            if (_thread != null && _thread.ManagedThreadId != Thread.CurrentThread.ManagedThreadId)
                _thread.Join();
        }

    }
}