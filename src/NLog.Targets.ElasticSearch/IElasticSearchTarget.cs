﻿using System.Collections.Generic;
using Elasticsearch.Net;
using NLog.Layouts;

namespace NLog.Targets.ElasticSearch
{
    public interface IElasticSearchTarget
    {
        /// <summary>
        /// Gets or sets a connection string name to retrieve the Uri from.
        /// 
        /// Use as an alternative to Uri
        /// </summary>
        string ConnectionStringName { get; set; }

        /// <summary>
        /// Gets or sets the elasticsearch uri, can be multiple comma separated.
        /// </summary>
        string Uri { get; set; }

        /// <summary>
        /// Set it to true if ElasticSearch uses BasicAuth
        /// </summary>
        bool RequireAuth { get; set; }

        /// <summary>
        /// Username for basic auth
        /// </summary>
        string Username { get; set; }

        /// <summary>
        /// Password for basic auth
        /// </summary>
        string Password { get; set; }

        /// <summary>
        /// Set it to true to disable proxy detection
        /// </summary>
        bool DisableAutomaticProxyDetection { get; set; }

        /// <summary>
        /// Gets or sets the name of the elasticsearch index to write to.
        /// </summary>
        Layout Index { get; set; }

        /// <summary>
        /// Gets or sets whether to include all properties of the log event in the document
        /// </summary>
        bool IncludeAllProperties { get; set; }

        /// <summary>
        /// Gets or sets a comma separated list of excluded properties when setting <see cref="IncludeAllProperties"/>
        /// </summary>
        string ExcludedProperties { get; set; }

        /// <summary>
        /// Gets or sets the document type for the elasticsearch index.
        /// </summary>
        Layout DocumentType { get; set; }

        /// <summary>
        /// Gets or sets a list of additional fields to add to the elasticsearch document.
        /// </summary>
        IList<Field> Fields { get; set; }
    }
}