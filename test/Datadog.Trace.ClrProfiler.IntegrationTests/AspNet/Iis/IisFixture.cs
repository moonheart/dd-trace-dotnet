﻿using System;
using System.IO;
using Datadog.Core.Tools;
using Datadog.Trace.TestHelpers;

namespace Datadog.Trace.ClrProfiler.IntegrationTests
{
    public sealed class RealIisFixture : IDisposable
    {
        private (System.Diagnostics.Process Process, string ConfigFile) _iisExpress;

        public MockTracerAgent Agent { get; private set; }

        public void TryStartIis(TestHelper helper, bool classicMode)
        {
            lock (this)
            {
                if (_iisExpress.Process == null)
                {
                    var initialAgentPort = TcpPortProvider.GetOpenPort();
                    Agent = new MockTracerAgent(initialAgentPort);

                    _iisExpress = helper.StartIISExpress(Agent.Port, HttpPort, classicMode);
                }
            }
        }

        public void Dispose()
        {
            Agent?.Dispose();

            lock (this)
            {
                if (_iisExpress.Process != null)
                {
                    try
                    {
                        if (!_iisExpress.Process.HasExited)
                        {
                            // sending "Q" to standard input does not work because
                            // iisexpress is scanning console key press, so just kill it.
                            // maybe try this in the future:
                            // https://github.com/roryprimrose/Headless/blob/master/Headless.IntegrationTests/IisExpress.cs
                            _iisExpress.Process.Kill();
                            _iisExpress.Process.WaitForExit(8000);
                        }
                    }
                    catch
                    {
                        // in some circumstances the HasExited property throws, this means the process probably hasn't even started correctly
                    }

                    _iisExpress.Process.Dispose();

                    try
                    {
                        File.Delete(_iisExpress.ConfigFile);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
