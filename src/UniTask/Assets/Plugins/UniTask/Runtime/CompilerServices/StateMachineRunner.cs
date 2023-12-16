﻿#pragma warning disable CS1591

using Cysharp.Threading.Tasks.Internal;
using System;
using System.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

namespace Cysharp.Threading.Tasks.CompilerServices
{
    // #ENABLE_IL2CPP in this file is to avoid bug of IL2CPP VM.
    // Issue is tracked on https://issuetracker.unity3d.com/issues/il2cpp-incorrect-results-when-calling-a-method-from-outside-class-in-a-struct
    // but currently it is labeled `Won't Fix`.

    internal interface IStateMachineRunner
    {
        Action MoveNext { get; }
        void Return();

#if ENABLE_IL2CPP
        Action ReturnAction { get; }
#endif
    }

    internal interface IStateMachineRunnerPromise : IUniTaskSource
    {
        Action MoveNext { get; }
        UniTask Task { get; }
        void SetResult();
        void SetException(Exception exception);
    }

    internal interface IStateMachineRunnerPromise<T> : IUniTaskSource<T>
    {
        Action MoveNext { get; }
        UniTask<T> Task { get; }
        void SetResult(T result);
        void SetException(Exception exception);
    }

    internal static class StateMachineUtility
    {
        // Get AsyncStateMachine internal state to check IL2CPP bug
        public static int GetState(IAsyncStateMachine stateMachine)
        {
            var info = stateMachine.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .First(x => x.Name.EndsWith("__state"));
            return (int)info.GetValue(stateMachine);
        }
    }

    internal sealed class AsyncUniTaskVoid<TStateMachine> : IStateMachineRunner, ITaskPoolNode<AsyncUniTaskVoid<TStateMachine>>, IUniTaskSource
        where TStateMachine : IAsyncStateMachine
    {
        static TaskPool<AsyncUniTaskVoid<TStateMachine>> pool;

#if ENABLE_IL2CPP
        public Action ReturnAction { get; }
#endif

        TStateMachine stateMachine;

        public Action MoveNext { get; }

        public AsyncUniTaskVoid()
        {
            MoveNext = Run;
#if ENABLE_IL2CPP
            ReturnAction = Return;
#endif
        }

        public static void SetStateMachine(ref TStateMachine stateMachine, ref IStateMachineRunner runnerFieldRef)
        {
            if (!pool.TryPop(out var result))
            {
                Profiler.BeginSample("Create AsyncUniTaskVoid<TStateMachine>");
                result = new AsyncUniTaskVoid<TStateMachine>();
                Profiler.EndSample();
            }
            TaskTracker.TrackActiveTask(result, 3);

            runnerFieldRef = result; // set runner before copied.
            result.stateMachine = stateMachine; // copy struct StateMachine(in release build).
        }

        static AsyncUniTaskVoid()
        {
            TaskPool.RegisterSizeGetter(typeof(AsyncUniTaskVoid<TStateMachine>), () => pool.Size);
        }

        AsyncUniTaskVoid<TStateMachine> nextNode;
        public ref AsyncUniTaskVoid<TStateMachine> NextNode => ref nextNode;

        public void Return()
        {
            TaskTracker.RemoveTracking(this);
            stateMachine = default;
            pool.TryPush(this);
        }

        [DebuggerHidden]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Run()
        {
            stateMachine.MoveNext();
        }

        // dummy interface implementation for TaskTracker.

        UniTaskStatus IUniTaskSource.GetStatus(short token)
        {
            return UniTaskStatus.Pending;
        }

        UniTaskStatus IUniTaskSource.UnsafeGetStatus()
        {
            return UniTaskStatus.Pending;
        }

        void IUniTaskSource.OnCompleted(Action<object> continuation, object state, short token)
        {
        }

        void IUniTaskSource.GetResult(short token)
        {
        }
    }

    internal sealed class AsyncUniTask<TStateMachine> : IStateMachineRunnerPromise, IUniTaskSource, ITaskPoolNode<AsyncUniTask<TStateMachine>>, ISilenceCancellation
        where TStateMachine : IAsyncStateMachine
    {
        private sealed class GCWrapper : IStateMachineRunnerPromise
        {
            AsyncUniTask<TStateMachine> task;
            short token;

            public GCWrapper(AsyncUniTask<TStateMachine> task, short token)
            {
                this.task = task;
                this.token = token;
                GC.KeepAlive(task);
            }

            ~GCWrapper()
            {
                if (task.core.Version == token)
                    task.TryReturn();
            }

            public UniTaskStatus GetStatus(short token) => task.GetStatus(token);

            public void OnCompleted(Action<object> continuation, object state, short token) 
                => task.OnCompleted(continuation, state, token);

            public void GetResult(short token) 
                => task.GetResult(token);

            public UniTaskStatus UnsafeGetStatus() 
                => task.UnsafeGetStatus();

            public Action MoveNext => task.MoveNext;
            public UniTask Task => task.Task;
            public void SetResult() => task.SetResult();

            public void SetException(Exception exception) => task.SetException(exception);
        }

        static TaskPool<AsyncUniTask<TStateMachine>> pool;

#if ENABLE_IL2CPP
        readonly Action returnDelegate;  
#endif
        public Action MoveNext { get; }

        TStateMachine stateMachine;
        UniTaskCompletionSourceCore<AsyncUnit> core;
        int lastVersionReturned = -1;

        AsyncUniTask()
        {
            MoveNext = Run;
#if ENABLE_IL2CPP
            returnDelegate = Return;
#endif
        }

        public static void SetStateMachine(ref TStateMachine stateMachine, ref IStateMachineRunnerPromise runnerPromiseFieldRef)
        {
            if (pool.Size < 16)
            {
                Profiler.BeginSample("Create AsyncUniTask<TStateMachine, T> Pool");
                while (pool.Size < 32)
                    pool.TryPush(new AsyncUniTask<TStateMachine>());
                Profiler.EndSample();
            }
            
            if (!pool.TryPop(out var result))
            {
                Profiler.BeginSample("Create AsyncUniTask<TStateMachine>");
                result = new AsyncUniTask<TStateMachine>();
                Profiler.EndSample();
            }
            TaskTracker.TrackActiveTask(result, 3);

            runnerPromiseFieldRef = new GCWrapper(result, result.core.Version); // set runner before copied.
            result.stateMachine = stateMachine; // copy struct StateMachine(in release build).
        }

        AsyncUniTask<TStateMachine> nextNode;
        public ref AsyncUniTask<TStateMachine> NextNode => ref nextNode;

        static AsyncUniTask()
        {
            TaskPool.RegisterSizeGetter(typeof(AsyncUniTask<TStateMachine>), () => pool.Size);
        }

        void Return()
        {
            lastVersionReturned = core.Version;
            
            TaskTracker.RemoveTracking(this);
            core.Reset();
            stateMachine = default;
            silenceCancellationRequested = false;
            pool.TryPush(this);
        }

        bool TryReturn()
        {
            if (lastVersionReturned == core.Version)
                return false;
            lastVersionReturned = core.Version;
            
            TaskTracker.RemoveTracking(this);
            core.Reset();
            stateMachine = default;
            silenceCancellationRequested = false;
            return pool.TryPush(this);
        }

        [DebuggerHidden]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Run()
        {
            stateMachine.MoveNext();
        }

        public UniTask Task
        {
            [DebuggerHidden]
            get
            {
                return new UniTask(this, core.Version);
            }
        }

        [DebuggerHidden]
        public void SetResult()
        {
            core.TrySetResult(AsyncUnit.Default);
        }

        [DebuggerHidden]
        public void SetException(Exception exception)
        {
            core.TrySetException(exception);
        }

        [DebuggerHidden]
        public void GetResult(short token)
        {
            try
            {
                if (silenceCancellationRequested && core.UnsafeGetStatus() == UniTaskStatus.Canceled)
                    return;
            
                core.GetResult(token);
            }
            finally
            {
#if ENABLE_IL2CPP
                // workaround for IL2CPP bug.
                PlayerLoopHelper.AddContinuation(PlayerLoopTiming.LastPostLateUpdate, returnDelegate);
#else
                TryReturn();
#endif
            }
        }

        [DebuggerHidden]
        public UniTaskStatus GetStatus(short token)
        {
            return core.GetStatus(token);
        }

        [DebuggerHidden]
        public UniTaskStatus UnsafeGetStatus()
        {
            return core.UnsafeGetStatus();
        }

        [DebuggerHidden]
        public void OnCompleted(Action<object> continuation, object state, short token)
        {
            core.OnCompleted(continuation, state, token);
        }

        public bool silenceCancellationRequested { get; set; }
    }

    internal sealed class AsyncUniTask<TStateMachine, T> : IStateMachineRunnerPromise<T>, IUniTaskSource<T>, ITaskPoolNode<AsyncUniTask<TStateMachine, T>>, ISilenceCancellation
        where TStateMachine : IAsyncStateMachine
    {
        private sealed class GCWrapper : IStateMachineRunnerPromise<T>
        {
            AsyncUniTask<TStateMachine, T> task;
            short token;

            public GCWrapper(AsyncUniTask<TStateMachine, T> task, short token)
            {
                this.task = task;
                this.token = token;
                GC.KeepAlive(task);
            }

            ~GCWrapper()
            {
                if (task.core.Version == token)
                    task.TryReturn();
            }

            public UniTaskStatus GetStatus(short token) => task.GetStatus(token);

            public void OnCompleted(Action<object> continuation, object state, short token) 
                => task.OnCompleted(continuation, state, token);

            T IUniTaskSource<T>.GetResult(short token) 
                => task.GetResult(token);

            public void GetResult(short token) 
                => task.GetResult(token);

            public UniTaskStatus UnsafeGetStatus() 
                => task.UnsafeGetStatus();

            public Action MoveNext => task.MoveNext;

            UniTask<T> IStateMachineRunnerPromise<T>.Task => task.Task;

            public void SetResult(T result) => task.SetResult(result);

            public void SetException(Exception exception) => task.SetException(exception);
        }
        
        static TaskPool<AsyncUniTask<TStateMachine, T>> pool;

#if ENABLE_IL2CPP
        readonly Action returnDelegate;  
#endif

        public Action MoveNext { get; }

        TStateMachine stateMachine;
        UniTaskCompletionSourceCore<T> core;
        int lastVersionReturned = -1;

        AsyncUniTask()
        {
            MoveNext = Run;
#if ENABLE_IL2CPP
            returnDelegate = Return;
#endif
        }

        public static void SetStateMachine(ref TStateMachine stateMachine, ref IStateMachineRunnerPromise<T> runnerPromiseFieldRef)
        {
            if (pool.Size < 16)
            {
                Profiler.BeginSample("Create AsyncUniTask<TStateMachine, T> Pool");
                while (pool.Size < 32)
                    pool.TryPush(new AsyncUniTask<TStateMachine, T>());
                Profiler.EndSample();
            }
            
            if (!pool.TryPop(out var result))
            {
                Profiler.BeginSample("Create AsyncUniTask<TStateMachine, T>");
                result = new AsyncUniTask<TStateMachine, T>();
                Profiler.EndSample();
            }
            TaskTracker.TrackActiveTask(result, 3);

            runnerPromiseFieldRef = new GCWrapper(result, result.core.Version); // set runner before copied.
            result.stateMachine = stateMachine; // copy struct StateMachine(in release build).
        }

        AsyncUniTask<TStateMachine, T> nextNode;
        public ref AsyncUniTask<TStateMachine, T> NextNode => ref nextNode;

        static AsyncUniTask()
        {
            TaskPool.RegisterSizeGetter(typeof(AsyncUniTask<TStateMachine, T>), () => pool.Size);
        }

        void Return()
        {
            lastVersionReturned = core.Version;
            TaskTracker.RemoveTracking(this);
            core.Reset();
            stateMachine = default;
            silenceCancellationRequested = false;
            pool.TryPush(this);
        }

        bool TryReturn()
        {
            if (lastVersionReturned == core.Version)
                return false;
            lastVersionReturned = core.Version;
            
            TaskTracker.RemoveTracking(this);
            core.Reset();
            stateMachine = default;
            silenceCancellationRequested = false;
            return pool.TryPush(this);
        }

        [DebuggerHidden]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Run()
        {
            // UnityEngine.Debug.Log($"MoveNext State:" + StateMachineUtility.GetState(stateMachine));
            stateMachine.MoveNext();
        }

        public UniTask<T> Task
        {
            [DebuggerHidden]
            get
            {
                return new UniTask<T>(this, core.Version);
            }
        }

        [DebuggerHidden]
        public void SetResult(T result)
        {
            core.TrySetResult(result);
        }

        [DebuggerHidden]
        public void SetException(Exception exception)
        {
            core.TrySetException(exception);
        }

        [DebuggerHidden]
        public T GetResult(short token)
        {
            try
            {
                if (silenceCancellationRequested && core.UnsafeGetStatus() == UniTaskStatus.Canceled)
                    return default;
            
                return core.GetResult(token);
            }
            finally
            {
#if ENABLE_IL2CPP
                // workaround for IL2CPP bug.
                PlayerLoopHelper.AddContinuation(PlayerLoopTiming.LastPostLateUpdate, returnDelegate);
#else
                TryReturn();
#endif
            }
        }

        [DebuggerHidden]
        void IUniTaskSource.GetResult(short token)
        {
            GetResult(token);
        }

        [DebuggerHidden]
        public UniTaskStatus GetStatus(short token)
        {
            return core.GetStatus(token);
        }

        [DebuggerHidden]
        public UniTaskStatus UnsafeGetStatus()
        {
            return core.UnsafeGetStatus();
        }

        [DebuggerHidden]
        public void OnCompleted(Action<object> continuation, object state, short token)
        {
            core.OnCompleted(continuation, state, token);
        }

        public bool silenceCancellationRequested { get; set; }
    }
}

