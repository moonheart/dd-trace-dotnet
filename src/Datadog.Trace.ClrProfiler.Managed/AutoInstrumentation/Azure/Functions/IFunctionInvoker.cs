// <copyright file="IFunctionInvoker.cs" company="Datadog">
// Unless explicitly stated otherwise all files in this repository are licensed under the Apache 2 License.
// This product includes software developed at Datadog (https://www.datadoghq.com/). Copyright 2017 Datadog, Inc.
// </copyright>

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Datadog.Trace.ClrProfiler.AutoInstrumentation.Azure.Functions
{
    /// <summary>
    /// Redis native client for duck typing
    /// </summary>
    public interface IFunctionInvoker
    {
        /// <summary>
        /// Gets the names of parameters, maybe useful for tags
        /// </summary>
        IReadOnlyList<string> ParameterNames { get; }

        /// <summary>
        /// Duck typed InvokeAsync
        /// </summary>
        Task<object> InvokeAsync(object instance, object[] arguments);
    }
}
