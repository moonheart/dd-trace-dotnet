﻿// <copyright file="CustomTestFramework.cs" company="Datadog">
// Unless explicitly stated otherwise all files in this repository are licensed under the Apache 2 License.
// This product includes software developed at Datadog (https://www.datadoghq.com/). Copyright 2017 Datadog, Inc.
// </copyright>

using Xunit;
using Xunit.Abstractions;

[assembly: TestFramework("Datadog.Trace.Tools.Analyzers.Tests.CustomTestFramework", "Datadog.Trace.Tools.Analyzers.Tests")]

namespace Datadog.Trace.Tools.Analyzers.Tests
{
    public class CustomTestFramework : Datadog.Trace.TestHelpers.CustomTestFramework
    {
        public CustomTestFramework(IMessageSink messageSink)
            : base(messageSink)
        {
        }
    }
}
