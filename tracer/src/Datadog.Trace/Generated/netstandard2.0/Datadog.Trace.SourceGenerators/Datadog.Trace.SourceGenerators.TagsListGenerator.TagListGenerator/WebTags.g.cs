﻿// <auto-generated/>
#nullable enable

using Datadog.Trace.Processors;
using Datadog.Trace.Tagging;

namespace Datadog.Trace.Tagging
{
    partial class WebTags
    {
        // SpanKindBytes = System.Text.Encoding.UTF8.GetBytes("span.kind");
        private static readonly byte[] SpanKindBytes = new byte[] { 115, 112, 97, 110, 46, 107, 105, 110, 100 };
        // HttpMethodBytes = System.Text.Encoding.UTF8.GetBytes("http.method");
        private static readonly byte[] HttpMethodBytes = new byte[] { 104, 116, 116, 112, 46, 109, 101, 116, 104, 111, 100 };
        // HttpRequestHeadersHostBytes = System.Text.Encoding.UTF8.GetBytes("http.request.headers.host");
        private static readonly byte[] HttpRequestHeadersHostBytes = new byte[] { 104, 116, 116, 112, 46, 114, 101, 113, 117, 101, 115, 116, 46, 104, 101, 97, 100, 101, 114, 115, 46, 104, 111, 115, 116 };
        // HttpUrlBytes = System.Text.Encoding.UTF8.GetBytes("http.url");
        private static readonly byte[] HttpUrlBytes = new byte[] { 104, 116, 116, 112, 46, 117, 114, 108 };
        // HttpStatusCodeBytes = System.Text.Encoding.UTF8.GetBytes("http.status_code");
        private static readonly byte[] HttpStatusCodeBytes = new byte[] { 104, 116, 116, 112, 46, 115, 116, 97, 116, 117, 115, 95, 99, 111, 100, 101 };

        public override string? GetTag(string key)
        {
            return key switch
            {
                "span.kind" => SpanKind,
                "http.method" => HttpMethod,
                "http.request.headers.host" => HttpRequestHeadersHost,
                "http.url" => HttpUrl,
                "http.status_code" => HttpStatusCode,
                _ => base.GetTag(key),
            };
        }

        public override void SetTag(string key, string value)
        {
            switch(key)
            {
                case "http.method": 
                    HttpMethod = value;
                    break;
                case "http.request.headers.host": 
                    HttpRequestHeadersHost = value;
                    break;
                case "http.url": 
                    HttpUrl = value;
                    break;
                case "http.status_code": 
                    HttpStatusCode = value;
                    break;
                default: 
                    base.SetTag(key, value);
                    break;
            }
        }

        public override void EnumerateTags<TProcessor>(ref TProcessor processor)
        {
            if (SpanKind is not null)
            {
                processor.Process(new TagItem<string>("span.kind", SpanKind, SpanKindBytes));
            }

            if (HttpMethod is not null)
            {
                processor.Process(new TagItem<string>("http.method", HttpMethod, HttpMethodBytes));
            }

            if (HttpRequestHeadersHost is not null)
            {
                processor.Process(new TagItem<string>("http.request.headers.host", HttpRequestHeadersHost, HttpRequestHeadersHostBytes));
            }

            if (HttpUrl is not null)
            {
                processor.Process(new TagItem<string>("http.url", HttpUrl, HttpUrlBytes));
            }

            if (HttpStatusCode is not null)
            {
                processor.Process(new TagItem<string>("http.status_code", HttpStatusCode, HttpStatusCodeBytes));
            }

            base.EnumerateTags(ref processor);
        }

        protected override void WriteAdditionalTags(System.Text.StringBuilder sb)
        {
            if (SpanKind is not null)
            {
                sb.Append("span.kind (tag):")
                  .Append(SpanKind)
                  .Append(',');
            }

            if (HttpMethod is not null)
            {
                sb.Append("http.method (tag):")
                  .Append(HttpMethod)
                  .Append(',');
            }

            if (HttpRequestHeadersHost is not null)
            {
                sb.Append("http.request.headers.host (tag):")
                  .Append(HttpRequestHeadersHost)
                  .Append(',');
            }

            if (HttpUrl is not null)
            {
                sb.Append("http.url (tag):")
                  .Append(HttpUrl)
                  .Append(',');
            }

            if (HttpStatusCode is not null)
            {
                sb.Append("http.status_code (tag):")
                  .Append(HttpStatusCode)
                  .Append(',');
            }

            base.WriteAdditionalTags(sb);
        }
    }
}
