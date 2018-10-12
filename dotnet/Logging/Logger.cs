﻿using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using WinSCP.Internal;

namespace WinSCP
{
    internal class Logger : IDisposable
    {
        public string LogPath { get { return _logPath; } set { SetLogPath(value); } }
        public int LogLevel { get { return _logLevel; } set { SetLogLevel(value); } }
        public bool Logging { get { return (_writter != null) && _writter.Enabled(); } }

        /// <inheritdoc />
        public Logger(ILogWriterFactory logWriterFactory)
        {
            _logWriterFactory = logWriterFactory;
        }

        public string GetAssemblyFilePath()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string path = null;
            string codeBase = assembly.CodeBase;
            string location = assembly.Location;
            // cannot use Uri.UnescapeDataString, because it treats some characters valid in
            // local path (like #) specially
            const string protocol = "file://";
            if (codeBase.StartsWith(protocol, StringComparison.OrdinalIgnoreCase))
            {
                path = codeBase.Substring(protocol.Length).Replace('/', '\\');
                if (!string.IsNullOrEmpty(path))
                {
                    if (path[0] == '\\')
                    {
                        path = path.Substring(1, path.Length - 1);
                    }
                    else
                    {
                        // UNC path
                        path = @"\\" + path;
                    }
                }
            }

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                if (File.Exists(location))
                {
                    path = location;
                }
                else
                {
                    WriteLine(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            "Cannot locate path of assembly [{0}] neither from its code base [{1}], nor from its location [{2}]",
                            assembly, codeBase, location));
                    path = null;
                }
            }

            return path;
        }

        private void CreateCounters()
        {
            try
            {
                PerformanceCounterCategory[] categories = PerformanceCounterCategory.GetCategories();
                foreach (PerformanceCounterCategory category in categories)
                {
                    if (category.CategoryName == "Processor")
                    {
                        string[] instances = category.GetInstanceNames();
                        foreach (string instance in instances)
                        {
                            AddCounter(new PerformanceCounter(category.CategoryName, "% Processor Time", instance));
                        }
                    }
                }

                AddCounter(new PerformanceCounter("Memory", "Available KBytes"));
            }
            catch (UnauthorizedAccessException)
            {
                WriteLine("Not authorized to get counters");
            }
            catch (Exception e)
            {
                WriteLine("Error getting counters: {0}", e);
            }
        }

        private void AddCounter(PerformanceCounter counter)
        {
            counter.NextValue();
            _performanceCounters.Add(counter);
        }

        public void WriteLine(string line)
        {
            lock (_logLock)
            {
                if (Logging)
                {
                    DoWriteLine(line);
                }
            }
        }

        public void WriteLine(string format, params object[] args)
        {
            lock (_logLock)
            {
                if (Logging)
                {
                    DoWriteLine(string.Format(CultureInfo.CurrentCulture, format, args));
                }
            }
        }

        public void WriteLineLevel(int level, string line)
        {
            if (LogLevel >= level)
            {
                WriteLine(line);
            }
        }

        public void WriteLineLevel(int level, string line, params object[] args)
        {
            if (LogLevel >= level)
            {
                WriteLine(line, args);
            }
        }

        private static int GetThread()
        {
            return Thread.CurrentThread.ManagedThreadId;
        }

        public void Indent()
        {
            lock (_logLock)
            {
                int threadId = GetThread();
                if (!_indents.TryGetValue(threadId, out int indent))
                {
                    indent = 0;
                }
                _indents[threadId] = indent + 1;
            }
        }

        public void Unindent()
        {
            lock (_logLock)
            {
                int threadId = GetThread();
                _indents[threadId]--;
            }
        }

        public void Dispose()
        {
            lock (_logLock)
            {
                if (Logging)
                {
                    WriteCounters();
                    WriteProcesses();
                    _writter.Dispose();
                    _writter = null;
                }

                foreach (PerformanceCounter counter in _performanceCounters)
                {
                    counter.Dispose();
                }
            }
        }

        public void WriteCounters()
        {
            if (Logging && (LogLevel >= 1))
            {
                try
                {
                    foreach (PerformanceCounter counter in _performanceCounters)
                    {
                        WriteLine("{0}{1}{2} = [{3}]",
                            counter.CounterName,
                            (string.IsNullOrEmpty(counter.InstanceName) ? string.Empty : "/"),
                            counter.InstanceName,
                            counter.NextValue());
                    }
                }
                catch (Exception e)
                {
                    WriteLine("Error reading counters: {0}", e);
                }
            }
        }

        public void WriteProcesses()
        {
            if (Logging && (LogLevel >= 1))
            {
                try
                {
                    Process[] processes = Process.GetProcesses();

                    foreach (Process process in processes)
                    {
                        WriteLine("{0}:{1} - {2} - {3}", process.Id, process.ProcessName, GetProcessStartTime(process), GetTotalProcessorTime(process));
                    }
                }
                catch (Exception e)
                {
                    WriteLine("Error logging processes: {0}", e);
                }
            }
        }

        private static object GetProcessStartTime(Process process)
        {
            try
            {
                return process.StartTime;
            }
            catch
            {
                return "???";
            }
        }

        private static object GetTotalProcessorTime(Process process)
        {
            try
            {
                return process.TotalProcessorTime;
            }
            catch
            {
                return "???";
            }
        }

        public Callstack CreateCallstack(object token = null)
        {
            return new Callstack(this, token);
        }

        public Callstack CreateCallstackAndLock()
        {
            return new CallstackAndLock(this, _lock);
        }

        //todo move this also to ILogWriter
        public Exception WriteException(Exception e)
        {
            lock (_logLock)
            {
                if (Logging)
                {
                    DoWriteLine(string.Format(CultureInfo.CurrentCulture, "Exception: {0}", e));
                    if (LogLevel >= 1)
                    {
                        DoWriteLine(new StackTrace().ToString());
                    }
                }
            }
            return e;
        }

        private int GetIndent()
        {
            if (!_indents.TryGetValue(GetThread(), out int indent))
            {
                indent = 0;
            }
            return indent;
        }

        private void DoWriteLine(string message)
        {
            int indent = GetIndent();

            _writter.WriteLine(indent, message);

        }

        private void SetLogPath(string value)
        {
            lock (_logLock)
            {
                //todo move checks logPath
                if (_logPath != value)
                {
                    Dispose();
                    _logPath = value;
                    if (!string.IsNullOrEmpty(_logPath))
                    {
                        _writter = _logWriterFactory.Create(_logPath);
                        WriteEnvironmentInfo();
                        if (_logLevel >= 1)
                        {
                            CreateCounters();
                        }
                    }
                }
            }
        }

        private void WriteEnvironmentInfo()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            WriteLine("Executing assembly: {0}", assembly);
            WriteLine("Executing assembly codebase: {0}", (assembly.CodeBase ?? "unknown"));
            WriteLine("Executing assembly location: {0}", (assembly.Location ?? "unknown"));
            Assembly entryAssembly = Assembly.GetEntryAssembly();
            WriteLine("Entry Assembly: {0}", (entryAssembly != null ? entryAssembly.ToString() : "unmanaged"));
            WriteLine("Operating system: {0}", Environment.OSVersion);
            WriteLine("User: {0}@{1}@{2}; Interactive: {3}", Environment.UserName, Environment.UserDomainName, Environment.MachineName, Environment.UserInteractive);
            WriteLine("Runtime: {0}", Environment.Version);
            WriteLine("Console encoding: Input: {0} ({1}); Output: {2} ({3})", Console.InputEncoding.EncodingName, Console.InputEncoding.CodePage, Console.OutputEncoding.EncodingName, Console.OutputEncoding.CodePage);
            WriteLine("Working directory: {0}", Environment.CurrentDirectory);
            string path = GetAssemblyFilePath();
            FileVersionInfo version = string.IsNullOrEmpty(path) ? null : FileVersionInfo.GetVersionInfo(path);
            WriteLine("Assembly path: {0}", path);
            WriteLine("Assembly product version: {0}", ((version != null) ? version.ProductVersion : "unknown"));
        }

        public static string LastWin32ErrorMessage()
        {
            return new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }

        private void SetLogLevel(int value)
        {
            if ((value < 0) || (value > 2))
            {
                throw WriteException(new ArgumentOutOfRangeException(string.Format(CultureInfo.CurrentCulture, "Logging level has to be in range 0-2")));
            }
            _logLevel = value;
        }

        private ILogWriter _writter;
        private string _logPath;
        private readonly Dictionary<int, int> _indents = new Dictionary<int, int>();
        private readonly object _logLock = new object();
        private readonly Lock _lock = new Lock();
        private List<PerformanceCounter> _performanceCounters = new List<PerformanceCounter>();
        private int _logLevel;
        private ILogWriterFactory _logWriterFactory;
    }
}
