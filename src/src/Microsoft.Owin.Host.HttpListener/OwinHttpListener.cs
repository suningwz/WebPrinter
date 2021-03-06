﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Owin.Host.HttpListener.RequestProcessing;

namespace Microsoft.Owin.Host.HttpListener
{
    using AppFunc = Func<IDictionary<string, object>, Task>;
    using LoggerFactoryFunc = Func<string, Func<TraceEventType, int, object, Exception, Func<object, Exception, string>, bool>>;
    using LoggerFunc = Func<TraceEventType, int, object, Exception, Func<object, Exception, string>, bool>;

    /// <summary>
    /// This wraps HttpListener and exposes it as an OWIN compatible server.
    /// </summary>
    public sealed class OwinHttpListener : IDisposable
    {
        private const int DefaultMaxRequests = Int32.MaxValue;
        private const long DefaultRequestQueueLength = 1000;  // Http.sys default.
        private static readonly int DefaultMaxAccepts = 5 * Environment.ProcessorCount;
        private static readonly bool IsMono = Type.GetType("Mono.Runtime") != null;
        private static readonly FieldInfo CookedPathField = typeof(HttpListenerRequest).GetField("m_CookedUrlPath", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo CookedQueryField = typeof(HttpListenerRequest).GetField("m_CookedUrlQuery", BindingFlags.NonPublic | BindingFlags.Instance);

        private Action _startNextRequestAsync;
        private Func<CatchInfo, CatchInfoBase<Task>.CatchResult> _startNextRequestError;
        private Func<CatchInfo, CatchInfoBase<Task>.CatchResult> _handleAcceptError;
        private Action<HttpListenerContext> _startProcessingRequest;
        private System.Net.HttpListener _listener;
        private IList<string> _basePaths;
        private AppFunc _appFunc;
        private DisconnectHandler _disconnectHandler;
        private IDictionary<string, object> _capabilities;
        private PumpLimits _pumpLimits;
        private int _currentOutstandingAccepts;
        private int _currentOutstandingRequests;
        private LoggerFunc _logger;
        private long? _requestQueueLength;

        /// <summary>
        /// Creates a listener wrapper that can be configured by the user before starting.
        /// </summary>
        internal OwinHttpListener()
        {
            _listener = new System.Net.HttpListener();
            _startNextRequestAsync = new Action(StartNextRequestAsync);
            _startNextRequestError = new Func<CatchInfo, CatchInfoBase<Task>.CatchResult>(StartNextRequestError);
            _startProcessingRequest = new Action<HttpListenerContext>(StartProcessingRequest);
            _handleAcceptError = new Func<CatchInfo, CatchInfoBase<Task>.CatchResult>(HandleAcceptError);
            SetRequestProcessingLimits(DefaultMaxAccepts, DefaultMaxRequests);
        }

        /// <summary>
        /// The HttpListener instance wrapped by this wrapper.
        /// </summary>
        public System.Net.HttpListener Listener
        {
            get { return _listener; }
        }

        private bool CanAcceptMoreRequests
        {
            get
            {
                PumpLimits limits = _pumpLimits;
                return (_currentOutstandingAccepts < limits.MaxOutstandingAccepts
                    && _currentOutstandingRequests < limits.MaxOutstandingRequests - _currentOutstandingAccepts);
            }
        }

        /// <summary>
        /// These are merged as one call because they should be swapped out atomically.
        /// This controls how many requests the server attempts to process concurrently.
        /// </summary>
        /// <param name="maxAccepts">The maximum number of pending request receives.</param>
        /// <param name="maxRequests">The maximum number of active requests being processed.</param>
        public void SetRequestProcessingLimits(int maxAccepts, int maxRequests)
        {
            _pumpLimits = new PumpLimits(maxAccepts, maxRequests);

            if (_listener.IsListening)
            {
                // Kick the pump in case we went from zero to non-zero limits.
                OffloadStartNextRequest();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="maxAccepts"></param>
        /// <param name="maxRequests"></param>
        [SuppressMessage("Microsoft.Design", "CA1021:AvoidOutParameters", MessageId = "0#", Justification = "By design")]
        [SuppressMessage("Microsoft.Design", "CA1021:AvoidOutParameters", MessageId = "1#", Justification = "By design")]
        public void GetRequestProcessingLimits(out int maxAccepts, out int maxRequests)
        {
            PumpLimits limits = _pumpLimits;
            maxAccepts = limits.MaxOutstandingAccepts;
            maxRequests = limits.MaxOutstandingRequests;
        }

        /// <summary>
        /// Sets the maximum number of requests that will be queued up in Http.Sys.
        /// </summary>
        /// <param name="limit"></param>
        public void SetRequestQueueLimit(long limit)
        {
            if (limit <= 0)
            {
                throw new ArgumentOutOfRangeException("limit", limit, string.Empty);
            }
            if ((!_requestQueueLength.HasValue && limit == DefaultRequestQueueLength)
                || (_requestQueueLength.HasValue && limit == _requestQueueLength.Value))
            {
                return;
            }

            _requestQueueLength = limit;

            SetRequestQueueLimit();
        }

        private void SetRequestQueueLimit()
        {
            // The listener must be active for this to work.  Call from Start after activating.
            // Platform check. This isn't supported on XP / Http.Sys v1.0, or Mono.
            if (IsMono || !_listener.IsListening || !_requestQueueLength.HasValue || Environment.OSVersion.Version.Major < 6)
            {
                return;
            }

            NativeMethods.SetRequestQueueLength(_listener, _requestQueueLength.Value);
        }

        /// <summary>
        /// Starts the listener and request processing threads.
        /// </summary>
        internal void Start(System.Net.HttpListener listener, AppFunc appFunc, IList<IDictionary<string, object>> addresses,
            IDictionary<string, object> capabilities, LoggerFactoryFunc loggerFactory)
        {
            Contract.Assert(_appFunc == null); // Start should only be called once
            Contract.Assert(listener != null);
            Contract.Assert(appFunc != null);
            Contract.Assert(addresses != null);

            _listener = listener;
            _appFunc = appFunc;
            _logger = LogHelper.CreateLogger(loggerFactory, typeof(OwinHttpListener));

            _basePaths = new List<string>();

            foreach (var address in addresses)
            {
                // build url from parts
                string scheme = address.Get<string>("scheme") ?? Uri.UriSchemeHttp;
                string host = address.Get<string>("host") ?? "localhost";
                string port = address.Get<string>("port") ?? "5000";
                string path = address.Get<string>("path") ?? string.Empty;

                // if port is present, add delimiter to value before concatenation
                if (!string.IsNullOrWhiteSpace(port))
                {
                    port = ":" + port;
                }

                // Assume http(s)://+:9090/BasePath/, including the first path slash.  May be empty. Must end with a slash.
                if (!path.EndsWith("/", StringComparison.Ordinal))
                {
                    // Http.Sys requires that the URL end in a slash
                    path += "/";
                }
                _basePaths.Add(path);

                // add a server for each url
                string url = scheme + "://" + host + port + path;
                _listener.Prefixes.Add(url);
            }

            _capabilities = capabilities;

            if (!_listener.IsListening)
            {
                _listener.Start();
            }

            SetRequestQueueLimit();

            _disconnectHandler = new DisconnectHandler(_listener, _logger);

            OffloadStartNextRequest();
        }

        private void OffloadStartNextRequest()
        {
            if (_listener.IsListening && CanAcceptMoreRequests)
            {
                Task.Factory.StartNew(_startNextRequestAsync)
                    .Catch(_startNextRequestError);
            }
        }

        private void StartNextRequestAsync()
        {
            if (!_listener.IsListening || !CanAcceptMoreRequests)
            {
                return;
            }

            Interlocked.Increment(ref _currentOutstandingAccepts);

            try
            {
                _listener.GetContextAsync()
                    .Then(_startProcessingRequest, runSynchronously: true)
                    .Catch(_handleAcceptError);
            }
            catch (ApplicationException ae)
            {
                // These come from the thread pool if HttpListener tries to call BindHandle after the listener has been disposed.
                HandleAcceptError(ae);
            }
            catch (HttpListenerException hle)
            {
                // These happen if HttpListener has been disposed
                HandleAcceptError(hle);
            }
            catch (ObjectDisposedException ode)
            {
                // These happen if HttpListener has been disposed
                HandleAcceptError(ode);
            }
        }

        private CatchInfoBase<Task>.CatchResult StartNextRequestError(CatchInfo errorInfo)
        {
            // StartNextRequestAsync should handle it's own exceptions.
            LogHelper.LogException(_logger, "Unexpected exception.", errorInfo.Exception);
            Contract.Assert(false, "Un-expected exception path: " + errorInfo.Exception.ToString());
#if DEBUG
            // Break into the debugger in case the message pump fails.
            System.Diagnostics.Debugger.Break();
#endif
            return errorInfo.Throw();
        }

        private CatchInfoBase<Task>.CatchResult HandleAcceptError(CatchInfo errorInfo)
        {
            HandleAcceptError(errorInfo.Exception);
            return errorInfo.Handled();
        }

        // Listener is disposed, but HttpListener.IsListening is not updated until the end of HttpListener.Dispose().
        private void HandleAcceptError(Exception ex)
        {
            Interlocked.Decrement(ref _currentOutstandingAccepts);
            LogHelper.LogException(_logger, "Accept", ex);
        }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Exception is logged")]
        private void StartProcessingRequest(HttpListenerContext context)
        {

            

            Interlocked.Decrement(ref _currentOutstandingAccepts);
            Interlocked.Increment(ref _currentOutstandingRequests);
            OffloadStartNextRequest();
            OwinHttpListenerContext owinContext = null;

            try
            {
                string pathBase, path, query;
                GetPathAndQuery(context.Request, out pathBase, out path, out query);
                owinContext = new OwinHttpListenerContext(context, pathBase, path, query, _disconnectHandler);
                PopulateServerKeys(owinContext.Environment);
                Contract.Assert(!owinContext.Environment.IsExtraDictionaryCreated,
                    "All keys set by the server should have reserved slots.");

                _appFunc(owinContext.Environment)
                    .Then((Func<Task>)owinContext.Response.CompleteResponseAsync, runSynchronously: true)
                    .Then(() =>
                    {
                       
                        owinContext.Response.Close();
                        EndRequest(owinContext, null);
                    }, runSynchronously: true)
                    .Catch(errorInfo =>
                    {
                        EndRequest(owinContext, errorInfo.Exception);
                        return errorInfo.Handled();
                    });
            }
            catch (Exception ex)
            {
                EndRequest(owinContext, ex);
            }
        }

        private void EndRequest(OwinHttpListenerContext owinContext, Exception ex)
        {
           
            Interlocked.Decrement(ref _currentOutstandingRequests);

            if (ex != null)
            {
                LogHelper.LogException(_logger, "Exception during request processing.", ex);
            }

            if (owinContext != null)
            {
                owinContext.End(ex);
                owinContext.Dispose();
            }

            // Make sure we start the next request on a new thread, need to prevent stack overflows.
            OffloadStartNextRequest();
        }

        // When the server is listening on multiple urls, we need to decide which one is the correct base path for this request.
        private void GetPathAndQuery(HttpListenerRequest request, out string pathBase, out string path, out string query)
        {
            string cookedPath;
            if (IsMono)
            {
                cookedPath = "/" + request.Url.GetComponents(UriComponents.Path, UriFormat.SafeUnescaped);
                query = request.Url.Query;

                //Mono。 空格是 + 号，不应该将%2B 直接转换为+ 号，+号是空格！！！！！！Mono在请求字符串中把%2B转换成了+号
                String plsuChar = "+";
                string htmlPlusChar = "%2B";
                if (!string.IsNullOrEmpty(query) && query.IndexOf(plsuChar) > -1)
                {
                    query = query.Replace(plsuChar, htmlPlusChar);
                }
            }
            else
            {
                cookedPath = (string)CookedPathField.GetValue(request) ?? string.Empty;
                query = (string)CookedQueryField.GetValue(request) ?? string.Empty;
            }

            if (!string.IsNullOrEmpty(query) && query[0] == '?')
            {
                query = query.Substring(1); // Drop the ?
            }

            // Find the split between path and pathBase.
            // This will only do full segment path matching because all _basePaths end in a '/'.
            bool endsInSlash = true;
            string bestMatch = "/";
            for (int i = 0; i < _basePaths.Count; i++)
            {
                string pathTest = _basePaths[i];
                if (pathTest.Length > bestMatch.Length)
                {
                    if (pathTest.Length <= cookedPath.Length
                        && cookedPath.StartsWith(pathTest, StringComparison.OrdinalIgnoreCase))
                    {
                        bestMatch = pathTest;
                        endsInSlash = true;
                    }
                    else if (pathTest.Length == cookedPath.Length + 1
                        && string.Compare(pathTest, 0, cookedPath, 0, cookedPath.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        // They matched exactly except for the trailing slash.
                        bestMatch = pathTest;
                        endsInSlash = false;
                    }
                }
            }

            // pathBase must be empty or start with a slash and not end with a slash (/pathBase)
            // path must start with a slash (/path)
            if (endsInSlash)
            {
                // Move the matched '/' from the end of the pathBase to the start of the path.
                pathBase = bestMatch.Substring(0, bestMatch.Length - 1);
                path = cookedPath.Substring(bestMatch.Length - 1);
            }
            else
            {
                pathBase = cookedPath;
                path = string.Empty;
            }
        }

        private void PopulateServerKeys(CallEnvironment env)
        {
            env.ServerCapabilities = _capabilities;
            env.Listener = _listener;
            env.OwinHttpListener = this;
        }

        internal void Stop()
        {
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Shuts down the listener and disposes it.
        /// </summary>
        public void Dispose()
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }

            ((IDisposable)_listener).Dispose();
        }
    }
}
