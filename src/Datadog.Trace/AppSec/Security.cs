// <copyright file="Security.cs" company="Datadog">
// Unless explicitly stated otherwise all files in this repository are licensed under the Apache 2 License.
// This product includes software developed at Datadog (https://www.datadoghq.com/). Copyright 2017 Datadog, Inc.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Datadog.Trace.AppSec.Transport;

namespace Datadog.Trace.AppSec
{
    /// <summary>
    /// The Secure is responsible cooridating app sec
    /// </summary>
    public class Security : IDatadogSecurity
    {
        private static Security _instance;
        private static bool _globalInstanceInitialized;
        private static object _globalInstanceLock = new();

        private InstrumentationGateway _instrumentationGateway;

        /// <summary>
        /// Initializes a new instance of the <see cref="Security"/> class with default settings.
        /// </summary>
        public Security()
            : this(null)
        {
        }

        internal Security(InstrumentationGateway instrumentationGateway = null)
        {
            _instrumentationGateway = instrumentationGateway ?? new InstrumentationGateway();

            _instrumentationGateway.InstrumentationGetwayEvent += InstrumentationGateway_InstrumentationGetwayEvent;

            var found = bool.TryParse(Environment.GetEnvironmentVariable("DD_ENABLE_SECURITY"), out var enabled);
            Enabled = (found && enabled);
        }

        /// <summary>
        /// Gets or sets the global <see cref="Security"/> instance.
        /// </summary>
        public static Security Instance
        {
            get
            {
                return LazyInitializer.EnsureInitialized(ref _instance, ref _globalInstanceInitialized, ref _globalInstanceLock);
            }

            set
            {
                lock (_globalInstanceLock)
                {
                    _instance = value;
                    _globalInstanceInitialized = true;
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether security is enabled
        /// </summary>
        public bool Enabled { get; }

        /// <summary>
        /// Gets <see cref="InstrumentationGateway"/> instance
        /// </summary>
        InstrumentationGateway IDatadogSecurity.InstrumentationGateway => _instrumentationGateway;

        private void RunWafAndReact(IReadOnlyDictionary<string, object> args, ITransport transport)
        {
            // run the fake WAF and execute the results
            if (args.TryGetValue("server.request.query", out var queryString) && queryString?.ToString()?.Contains("database()") == true)
            {
                transport.Block();
            }
        }

        private void InstrumentationGateway_InstrumentationGetwayEvent(object sender, InstrumentationGatewayEventArgs e)
        {
            // if we're not enabled events shouldn't be generated, but this is belt and braces
            if (Enabled)
            {
                RunWafAndReact(e.EventData, e.Transport);
            }
        }
    }
}