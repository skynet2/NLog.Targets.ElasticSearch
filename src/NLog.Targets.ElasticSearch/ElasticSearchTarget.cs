using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using Elasticsearch.Net;
using Nest;
using Nest.JsonNetSerializer;
using NLog.Common;
using NLog.Config;
using NLog.Layouts;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace NLog.Targets.ElasticSearch
{
    [Target("ElasticSearch")]
    public class ElasticSearchTarget : TargetWithLayout, IElasticSearchTarget
    {
        private IElasticLowLevelClient _client;

        private List<string> _excludedProperties = new List<string>(new[]
        {
            "CallerMemberName", "CallerFilePath", "CallerLineNumber", "MachineName", "ThreadId"
        });

        private readonly List<string> _exludedMsProps = new List<string>(new[]
        {
            "EventId_Id", "EventId_Name", "FullPath", "HashAlgorithm", "HashAlgorithmProvider",
            "FromType", "ToType", "commandTimeout", "newline", "options", "version", "KeyId", "FullName",
            "OutputFormatter", "EventId", "PathBase", "InputFormatter", "ExpirationDate", "Arguments",
            "ValidationState"
        });
        
        private readonly List<string> _exludedMsExceptionProps = new List<string>(new[]
        {
            "WatsonBuckets", "HelpURL", "RemoteStackIndex", "RemoteStackTraceString"
        });

        /// <summary>
        /// Gets or sets a connection string name to retrieve the Uri from.
        /// 
        /// Use as an alternative to Uri
        /// </summary>
        public string ConnectionStringName { get; set; }

        /// <summary>
        /// Gets or sets the elasticsearch uri, can be multiple comma separated.
        /// </summary>
        public Layout Uri { get; set; } = "http://localhost:9200";

        /// <summary>
        /// Set it to true if ElasticSearch uses BasicAuth
        /// </summary>
        public bool RequireAuth { get; set; }

        /// <summary>
        /// Username for basic auth
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// Password for basic auth
        /// </summary>
        public string Password { get; set; }

        /// <summary>
        /// Set it to true to disable proxy detection
        /// </summary>
        public bool DisableAutomaticProxyDetection { get; set; }

        /// <summary>
        /// Gets or sets the name of the elasticsearch index to write to.
        /// </summary>
        public Layout Index { get; set; } = "logstash-${date:format=yyyy.MM.dd}";

        /// <summary>
        /// Gets or sets whether to include all properties of the log event in the document
        /// </summary>
        public bool IncludeAllProperties { get; set; }

        /// <summary>
        /// Gets or sets a comma separated list of excluded properties when setting <see cref="IElasticSearchTarget.IncludeAllProperties"/>
        /// </summary>
        public string ExcludedProperties { get; set; }

        public bool ExcludeMsProperties { get; set; } = true;
        /// <summary>
        /// Gets or sets the document type for the elasticsearch index.
        /// </summary>
        [RequiredParameter]
        public Layout DocumentType { get; set; } = "logevent";

        /// <summary>
        /// Gets or sets a list of additional fields to add to the elasticsearch document.
        /// </summary>
        [ArrayParameter(typeof(Field), "field")]
        public IList<Field> Fields { get; set; } = new List<Field>();

        /// <summary>
        /// Gets or sets if exceptions will be rethrown.
        /// 
        /// Set it to true if ElasticSearchTarget target is used within FallbackGroup target (https://github.com/NLog/NLog/wiki/FallbackGroup-target).
        /// </summary>
        public bool ThrowExceptions { get; set; }

        public ElasticSearchTarget()
        {
            Name = "ElasticSearch";
        }

        protected override void InitializeTarget()
        {
            base.InitializeTarget();

            if (!string.IsNullOrEmpty(ExcludedProperties))
                _excludedProperties = ExcludedProperties.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
        }

        private void EnsureConnectionOpen()
        {
            if(_client == null)
            {
                var uri = ConnectionStringName.GetConnectionString() ?? Uri;
                var nodes = uri.Render(new LogEventInfo()).Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries).Select(url => new Uri(url));
                var connectionPool = new StaticConnectionPool(nodes);

                var config =
                    new ConnectionSettings(connectionPool, sourceSerializer: (builtin, settings) => new JsonNetSerializer(
                                               builtin, settings,
                                               () => new JsonSerializerSettings
                                               {
                                                   NullValueHandling = NullValueHandling.Include,
                                                   ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                                               },
                                               resolver => resolver.NamingStrategy = new SnakeCaseNamingStrategy()
                                           ));

                if (RequireAuth)
                    config.BasicAuthentication(Username, Password);

                if (DisableAutomaticProxyDetection)
                    config.DisableAutomaticProxyDetection();
                
                _client = new ElasticLowLevelClient(config);
            }
        }

        protected override void Write(AsyncLogEventInfo logEvent)
        {
            SendBatch(new[] {logEvent});
        }

        protected override void Write(IList<AsyncLogEventInfo> logEvents)
        {
            SendBatch(logEvents);
        }

        private void SendBatch(ICollection<AsyncLogEventInfo> logEvents)
        {
            EnsureConnectionOpen();
            
            try
            {
                var payload = FormPayload(logEvents);

                var result = _client.Bulk<BytesResponse>(payload);

                if (!result.Success)
                {
                    var errorMessage = result.OriginalException?.Message ??
                                       "No error message. Enable Trace logging for more information.";
                    InternalLogger.Error(
                        $"Failed to send log messages to elasticsearch: status={result.HttpStatusCode}, message=\"{errorMessage}\"");
                    InternalLogger.Trace($"Failed to send log messages to elasticsearch: result={result}");

                    if (result.OriginalException != null)
                        throw result.OriginalException;
                }

                foreach (var ev in logEvents)
                {
                    ev.Continuation(null);
                }
            }
            catch (Exception ex)
            {
                InternalLogger.Error($"Error while sending log messages to elasticsearch: message=\"{ex.Message}\"");

                foreach (var ev in logEvents)
                {
                    ev.Continuation(ex);
                }
            }
        }

        private PostData FormPayload(ICollection<AsyncLogEventInfo> logEvents)
        {
            var payload = new List<object>(logEvents.Count);

            foreach (var ev in logEvents)
            {
                var logEvent = ev.LogEvent;

                var document = new Dictionary<string, object>
                {
                    {"level", logEvent.Level.Name},
                    {"message", Layout.Render(logEvent)}
                };

                if (logEvent.Exception != null)
                {
                    document["exception"] = FilterException(logEvent.Exception);
                }

                foreach (var field in Fields)
                {
                    var renderedField = field.Layout.Render(logEvent);

                    if (!string.IsNullOrWhiteSpace(renderedField))
                        document[field.Name] = renderedField.ToSystemType(field.LayoutType, logEvent.FormatProvider);
                }

                if (!document.Keys.Any(x => x.Equals("date", StringComparison.CurrentCultureIgnoreCase)))
                    document["date"] = logEvent.TimeStamp;


                if (!document.Keys.Any(x => x.Equals("logger", StringComparison.CurrentCultureIgnoreCase)))
                    document["logger"] = logEvent.LoggerName;

                if (IncludeAllProperties && logEvent.Properties.Any())
                {
                    var prop = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    document["properties"] = prop;

                    foreach (var p in logEvent.Properties)
                    {
                        var propertyKey = p.Key.ToString();

                        if (_excludedProperties.Contains(propertyKey))
                            continue;

                        if(ExcludeMsProperties && logEvent.LoggerName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) 
                           && _exludedMsProps.Contains(propertyKey, StringComparer.OrdinalIgnoreCase))
                            continue;

                        if (prop.ContainsKey(propertyKey))
                            continue;

                        prop[propertyKey] = p.Value;
                    }
                }

                var index = Index.Render(logEvent).ToLowerInvariant();
                var type = DocumentType.Render(logEvent);

                payload.Add(new {index = new {_index = index, _type = type}});
                payload.Add(document);
            }

            return PostData.MultiJson(payload);
        }

        private Dictionary<string, object> FilterException(Exception exception)
        {
            var ex = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in exception.GetType().GetProperties())
            {
                var propertyKey = p.Name;

                if (_excludedProperties.Contains(propertyKey))
                    continue;

                if(ExcludeMsProperties && _exludedMsExceptionProps.Contains(propertyKey, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (ex.ContainsKey(propertyKey))
                    continue;

                var val = p.GetValue(exception);

                if(propertyKey == "InnerException" && val != null)
                    ex[propertyKey] = FilterException((Exception)val);    
                else
                    ex[propertyKey] = val;
            }
            
            return ex;
        }
    }
}