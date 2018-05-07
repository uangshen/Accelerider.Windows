﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Accelerider.Windows.Infrastructure.Interfaces;

namespace Accelerider.Windows.Infrastructure.TransportImpls
{
    internal class TransportScheduler<T> where T : ITransportTask
    {
        private class Listener
        {
            private readonly TransportScheduler<T> _scheduler;

            public Listener(TransportScheduler<T> scheduler) => _scheduler = scheduler;

            public async void OnStatusChanged(ITransportTask sender, StatusChangedEventArgs e) =>
                await OnStatusChangedInternal((T)sender, e);

            private async Task OnStatusChangedInternal(T task, StatusChangedEventArgs e)
            {
                if (e.NewStatus == TransportStatus.Completed)
                {
                    _scheduler._transportingQueue.Remove(task);
                    _scheduler._completedQueue.Enqueue(task);
                }

                if (e.NewStatus != TransportStatus.Transporting)
                {
                    await _scheduler.PromoteAsync();
                }

                if (!task.IsDisposed) return;

                if (_scheduler._pendingQueue.Remove(task) ||
                    _scheduler._transportingQueue.Remove(task) ||
                    _scheduler._completedQueue.Remove(task))
                {
                    await _scheduler.PromoteAsync();
                }
            }
        }

        private const int MaxParallelTaskCount = 4; // TODO: Move to configure file.

        private readonly ConcurrentTaskQueue<T> _pendingQueue = new ConcurrentTaskQueue<T>();
        private readonly ConcurrentTaskQueue<T> _transportingQueue = new ConcurrentTaskQueue<T>();
        private readonly ConcurrentTaskQueue<T> _completedQueue = new ConcurrentTaskQueue<T>();

        private bool _isActived;
        private bool _isPromoting;

        public async Task StartAsync()
        {
            _isActived = true;
            await PromoteAsync();
        }

        public async Task RecordAsync(T task)
        {
            task.StatusChanged += new Listener(this).OnStatusChanged;
            switch (task.Status)
            {
                case TransportStatus.Ready:
                case TransportStatus.Suspended:
                case TransportStatus.Faulted:
                    _pendingQueue.Enqueue(task);
                    await PromoteAsync();
                    break;
                case TransportStatus.Transporting:
                    await task.SuspendAsync();
                    _pendingQueue.Enqueue(task);
                    await PromoteAsync();
                    break;
                case TransportStatus.Completed:
                    _completedQueue.Enqueue(task);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public IEnumerable<T> GetAllTasks() => _pendingQueue.Union(_transportingQueue).Union(_completedQueue);

        public async Task<(IEnumerable<T> uncompletedTasks, IEnumerable<T> completedTasks)> ShutdownAsync()
        {
            while (_transportingQueue.Any())
            {
                await _transportingQueue.Dequeue().SuspendAsync();
            }

            return (_pendingQueue, _completedQueue);
        }

        private async Task PromoteAsync()
        {
            if (!_isActived || _isPromoting) return;

            _isPromoting = true;
            while (_transportingQueue.Count < MaxParallelTaskCount)
            {
                var pendingTask = _pendingQueue.Dequeue(task => task.Status == TransportStatus.Ready);
                if (pendingTask == null) return;

                try
                {
                    await pendingTask.StartAsync();
                    _transportingQueue.Enqueue(pendingTask);
                }
                catch
                {
                    _pendingQueue.Enqueue(pendingTask);
                }
            }
            _isPromoting = false;
        }
    }
}
