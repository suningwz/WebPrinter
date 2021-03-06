using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AspNet.WebApi.Owin
{

    using AppFunc = Func<IDictionary<string, object>, Task>;

    internal abstract class OwinHttpMessageStep
	{
		/// <summary>
		/// Incoming call from prior OWIN middleware
		/// </summary>
		
		public Task Invoke(IDictionary<string, object> env)
		{
			return this.Invoke(env, OwinHttpMessageUtilities.GetRequestMessage(env), OwinHttpMessageUtilities.GetCancellationToken(env));
		}

		/// <summary>
		/// Call to process HttpRequestMessage at current step
		/// </summary>
		
		protected abstract Task Invoke(IDictionary<string, object> env, HttpRequestMessage requestMessage, CancellationToken cancellationToken);

		/// <summary>
		/// Present in OWIN pipeline following a CallHttpMessageInvoker step in order
		/// to call AppFunc with original environment. 
		/// </summary>
		
		public class CallAppFunc : OwinHttpMessageStep
		{

            private readonly AppFunc _next;

            public CallAppFunc(AppFunc next)
            {
                _next = next;
            }

            protected override Task Invoke(
                IDictionary<string, object> env,
                HttpRequestMessage requestMessage,
                CancellationToken cancellationToken)
            {
                return _next.Invoke(env);
            }

        }

		/// <summary>
		/// Present in OWIN pipeline to call HttpMessageInvoker. Invoker represents
		/// an entire HttpServer pipeline which will execute request and response to completion.
		/// The response message will either be transmitted without additional OWIN pipeline processing,
		/// or in the case of a 404 status the HttpResponseMessage is ignored and processing continues 
		/// to the _next step through CallAppFunc.Invoke which passes the original environment to the rest of
		/// the OWIN pipeline.
		/// </summary>
		
		public class CallHttpMessageInvoker : OwinHttpMessageStep
		{
			
			public CallHttpMessageInvoker(OwinHttpMessageStep next, HttpMessageInvoker invoker)
			{
				this._next = next;
				this._invoker = invoker;
			}

			
			protected override Task Invoke(IDictionary<string, object> env, HttpRequestMessage requestMessage, CancellationToken cancellationToken)
			{
				return this._invoker.SendAsync(requestMessage, cancellationToken).Then(delegate(HttpResponseMessage responseMessage)
				{
					if (responseMessage.StatusCode == HttpStatusCode.NotFound)
					{
						responseMessage.Dispose();
						return this._next.Invoke(env, requestMessage, cancellationToken);
					}
					Task result;
					try
					{
						result = OwinHttpMessageUtilities.SendResponseMessage(env, responseMessage, cancellationToken).Finally(delegate
						{
							responseMessage.Dispose();
						}, true);
					}
					catch (Exception)
					{
						responseMessage.Dispose();
						throw;
					}
					return result;
				}, default(CancellationToken), false);
			}

			
			private readonly HttpMessageInvoker _invoker;

			
			private readonly OwinHttpMessageStep _next;
		}
	}
}
