namespace Datadog.Trace.ClrProfiler.AutoInstrumentation.Http.CurlHandler
{
    /// <summary>
    /// HttpResponseMessage interface for ducktyping
    /// </summary>
    public interface IHttpResponseMessage
    {
        /// <summary>
        /// Gets the status code of the http response
        /// </summary>
        int StatusCode { get; }
    }
}
