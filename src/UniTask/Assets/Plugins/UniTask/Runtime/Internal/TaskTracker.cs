#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks.Internal;
using UnityEngine.Pool;
using UnityEngine.Profiling;

namespace Cysharp.Threading.Tasks
{
    // public for add user custom.

    public static class TaskTracker
    {
#if UNITY_EDITOR

        static int trackingId = 0;

        public const string EnableAutoReloadKey = "UniTaskTrackerWindow_EnableAutoReloadKey";
        public const string EnableTrackingKey = "UniTaskTrackerWindow_EnableTrackingKey";
        public const string EnableStackTraceKey = "UniTaskTrackerWindow_EnableStackTraceKey";

        public static class EditorEnableState
        {
            static bool enableAutoReload;
            public static bool EnableAutoReload
            {
                get { return enableAutoReload; }
                set
                {
                    enableAutoReload = value;
                    UnityEditor.EditorPrefs.SetBool(EnableAutoReloadKey, value);
                }
            }

            static bool enableTracking;
            public static bool EnableTracking
            {
                get { return enableTracking; }
                set
                {
                    enableTracking = value;
                    UnityEditor.EditorPrefs.SetBool(EnableTrackingKey, value);
                }
            }

            static bool enableStackTrace;
            public static bool EnableStackTrace
            {
                get { return enableStackTrace; }
                set
                {
                    enableStackTrace = value;
                    UnityEditor.EditorPrefs.SetBool(EnableStackTraceKey, value);
                }
            }
        }

#endif

        private readonly struct FrameData
        {
            public readonly bool isValid;
            public readonly int offset;
            public readonly int ilOffset;
            public readonly MethodBase method;
           
            // mono internal
            private static readonly MethodInfo get_frame_info_methodInfo 
                = typeof(StackFrame).GetMethod("get_frame_info", BindingFlags.Static | BindingFlags.NonPublic);

            private delegate bool get_frame_info(int depth, bool fileInfo, ref MethodBase methodBase,
                ref int offset, ref int ilOffset, ref string fileName, ref int lineNumber, ref int columnNumber);
            
            private static readonly get_frame_info get_frame_info_delegate 
                = (get_frame_info)Delegate.CreateDelegate(typeof(get_frame_info), get_frame_info_methodInfo);
            
            public FrameData(int depth) : this()
            {
                int lineNumber = 0, columnNumber = 0;
                string fileName = null;
                isValid = get_frame_info_delegate(depth, false, ref method, ref offset, ref ilOffset, 
                    ref fileName, ref lineNumber, ref columnNumber);
            }

            public override string ToString()
            {
                var sb = new StringBuilder();
                if (method != null)
                {
                    if (method.DeclaringType != null) 
                        sb.Append(method.DeclaringType.FullName);
                    sb.Append(".");
                    sb.Append(method.Name);
                }
                else
                {
                    sb.Append("(unknown)");
                }
                
                if (offset != 0)
                {
                    sb.Append(" (:");
                    sb.Append(offset);
                    sb.Append(")");
                }
                
                return sb.ToString();
            }
        }


        static List<KeyValuePair<IUniTaskSource, (string formattedType, int trackingId, DateTime addTime, List<string> stackFrames, string stackTrace)>> 
            listPool = new();

        static readonly WeakDictionary<IUniTaskSource, (string formattedType, int trackingId, DateTime addTime, List<string> stackFrames, string stackTrace)> 
            tracking = new();
        
        static readonly Dictionary<(MethodBase methodBase, int offset), string> stackFrameStringCache = new();
        
        static readonly Dictionary<Type, string> typeNameCache = new();
        
        static readonly Dictionary<MethodBase, bool> isHiddenCache = new();
        
        private static List<string> GetStackFrames(int depth)
        {
            Profiler.BeginSample("UniTaskTracker: Allocate String Pool");
            var frames = ListPool<string>.Get();
            Profiler.EndSample();

            do
            {
                Profiler.BeginSample("UniTaskTracker: Create FrameData");
                var frame = new FrameData(depth++);
                Profiler.EndSample();
                
                if (!frame.isValid)
                    break;

                if (frame.method == null)
                    continue;

                if (!isHiddenCache.TryGetValue(frame.method, out var isHidden))
                {
                    Profiler.BeginSample("UniTaskTracker: Check DebuggerHiddenAttribute");
                    isHiddenCache[frame.method] = isHidden = frame.method.IsDefined(typeof(DebuggerHiddenAttribute), true);
                    Profiler.EndSample();
                }
                if (isHidden)
                    continue;
                
                if (!stackFrameStringCache.TryGetValue((frame.method, frame.offset), out var cached))
                {
                    Profiler.BeginSample("UniTaskTracker: Create Frame String");
                    cached = frame.ToString();
                    Profiler.EndSample();
                    stackFrameStringCache.Add((frame.method, frame.offset), cached);
                }
                frames.Add(cached);
                
            } while (true);
            
            frames.Reverse();
            return frames;
        }

        [Conditional("UNITY_EDITOR")]
        public static void TrackActiveTask(IUniTaskSource task, int skipFrame)
        {
#if UNITY_EDITOR
            dirty = true;
            if (!EditorEnableState.EnableTracking) return;
            
            Profiler.BeginSample("UniTaskTracker: TrackActiveTask");
            var stackTrace = EditorEnableState.EnableStackTrace ? new StackTrace(skipFrame, true).CleanupAsyncStackTrace() : "";

            var type = task.GetType();
            string typeName;
            if (EditorEnableState.EnableStackTrace)
            {
                var sb = new StringBuilder();
                TypeBeautify(type, sb);
                typeName = sb.ToString();
            }
            else
            {
                if (!typeNameCache.TryGetValue(type, out typeName))
                    typeName = typeNameCache[type] = type.Name;
            }
            
            tracking.TryAdd(task, (typeName, Interlocked.Increment(ref trackingId), DateTime.UtcNow,
                GetStackFrames(skipFrame + 1), stackTrace));
            Profiler.EndSample();
#endif
        }

        [Conditional("UNITY_EDITOR")]
        public static void RemoveTracking(IUniTaskSource task)
        {
#if UNITY_EDITOR
            dirty = true;
            if (!EditorEnableState.EnableTracking) return;
            
            if (tracking.TryGetValue(task, out var value) && value.stackFrames != null)
                ListPool<string>.Release(value.stackFrames);
            var success = tracking.TryRemove(task);
#endif
        }
        
        public static List<string> GetTaskFrames(IUniTaskSource task)
        {
#if UNITY_EDITOR
            if (!EditorEnableState.EnableTracking) return null;
            return !tracking.TryGetValue(task, out var value) ? null : value.stackFrames;

#else
            return null;
#endif
        }

        static bool dirty;

        public static bool CheckAndResetDirty()
        {
            var current = dirty;
            dirty = false;
            return current;
        }

        /// <summary>(trackingId, awaiterType, awaiterStatus, createdTime, stackTrace)</summary>
        public static void ForEachActiveTask(Action<int, string, UniTaskStatus, DateTime, string> action)
        {
            lock (listPool)
            {
                var count = tracking.ToList(ref listPool, clear: false);
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        action(listPool[i].Value.trackingId, listPool[i].Value.formattedType, listPool[i].Key.UnsafeGetStatus(), listPool[i].Value.addTime, listPool[i].Value.stackTrace);
                        listPool[i] = default;
                    }
                }
                catch
                {
                    listPool.Clear();
                    throw;
                }
            }
        }

        static void TypeBeautify(Type type, StringBuilder sb)
        {
            if (type.IsNested)
            {
                // TypeBeautify(type.DeclaringType, sb);
                sb.Append(type.DeclaringType.Name.ToString());
                sb.Append(".");
            }

            if (type.IsGenericType)
            {
                var genericsStart = type.Name.IndexOf("`");
                if (genericsStart != -1)
                {
                    sb.Append(type.Name.Substring(0, genericsStart));
                }
                else
                {
                    sb.Append(type.Name);
                }
                sb.Append("<");
                var first = true;
                foreach (var item in type.GetGenericArguments())
                {
                    if (!first)
                    {
                        sb.Append(", ");
                    }
                    first = false;
                    TypeBeautify(item, sb);
                }
                sb.Append(">");
            }
            else
            {
                sb.Append(type.Name);
            }
        }

        //static string RemoveUniTaskNamespace(string str)
        //{
        //    return str.Replace("Cysharp.Threading.Tasks.CompilerServices", "")
        //        .Replace("Cysharp.Threading.Tasks.Linq", "")
        //        .Replace("Cysharp.Threading.Tasks", "");
        //}
    }
}

