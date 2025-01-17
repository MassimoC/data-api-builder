// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO.Abstractions;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Service;
using CommandLine;
using Microsoft.Extensions.Logging;
using static Cli.Utils;

namespace Cli.Commands
{
    /// <summary>
    /// Init command options
    /// </summary>
    [Verb("init", isDefault: false, HelpText = "Initialize configuration file.", Hidden = false)]
    public class InitOptions : Options
    {
        public InitOptions(
            DatabaseType databaseType,
            string? connectionString,
            string? cosmosNoSqlDatabase,
            string? cosmosNoSqlContainer,
            string? graphQLSchemaPath,
            bool setSessionContext,
            HostMode hostMode,
            IEnumerable<string>? corsOrigin,
            string authenticationProvider,
            string? audience = null,
            string? issuer = null,
            string restPath = RestRuntimeOptions.DEFAULT_PATH,
            bool restDisabled = false,
            string graphQLPath = GraphQLRuntimeOptions.DEFAULT_PATH,
            bool graphqlDisabled = false,
            string? config = null)
            : base(config)
        {
            DatabaseType = databaseType;
            ConnectionString = connectionString;
            CosmosNoSqlDatabase = cosmosNoSqlDatabase;
            CosmosNoSqlContainer = cosmosNoSqlContainer;
            GraphQLSchemaPath = graphQLSchemaPath;
            SetSessionContext = setSessionContext;
            HostMode = hostMode;
            CorsOrigin = corsOrigin;
            AuthenticationProvider = authenticationProvider;
            Audience = audience;
            Issuer = issuer;
            RestPath = restPath;
            RestDisabled = restDisabled;
            GraphQLPath = graphQLPath;
            GraphQLDisabled = graphqlDisabled;
        }

        [Option("database-type", Required = true, HelpText = "Type of database to connect. Supported values: mssql, cosmosdb_nosql, cosmosdb_postgresql, mysql, postgresql")]
        public DatabaseType DatabaseType { get; }

        [Option("connection-string", Required = false, HelpText = "(Default: '') Connection details to connect to the database.")]
        public string? ConnectionString { get; }

        [Option("cosmosdb_nosql-database", Required = false, HelpText = "Database name for Cosmos DB for NoSql.")]
        public string? CosmosNoSqlDatabase { get; }

        [Option("cosmosdb_nosql-container", Required = false, HelpText = "Container name for Cosmos DB for NoSql.")]
        public string? CosmosNoSqlContainer { get; }

        [Option("graphql-schema", Required = false, HelpText = "GraphQL schema Path.")]
        public string? GraphQLSchemaPath { get; }

        [Option("set-session-context", Default = false, Required = false, HelpText = "Enable sending data to MsSql using session context.")]
        public bool SetSessionContext { get; }

        [Option("host-mode", Default = HostMode.Production, Required = false, HelpText = "Specify the Host mode - Development or Production")]
        public HostMode HostMode { get; }

        [Option("cors-origin", Separator = ',', Required = false, HelpText = "Specify the list of allowed origins.")]
        public IEnumerable<string>? CorsOrigin { get; }

        [Option("auth.provider", Default = "StaticWebApps", Required = false, HelpText = "Specify the Identity Provider.")]
        public string AuthenticationProvider { get; }

        [Option("auth.audience", Required = false, HelpText = "Identifies the recipients that the JWT is intended for.")]
        public string? Audience { get; }

        [Option("auth.issuer", Required = false, HelpText = "Specify the party that issued the jwt token.")]
        public string? Issuer { get; }

        [Option("rest.path", Default = RestRuntimeOptions.DEFAULT_PATH, Required = false, HelpText = "Specify the REST endpoint's default prefix.")]
        public string RestPath { get; }

        [Option("rest.disabled", Default = false, Required = false, HelpText = "Disables REST endpoint for all entities.")]
        public bool RestDisabled { get; }

        [Option("graphql.path", Default = GraphQLRuntimeOptions.DEFAULT_PATH, Required = false, HelpText = "Specify the GraphQL endpoint's default prefix.")]
        public string GraphQLPath { get; }

        [Option("graphql.disabled", Default = false, Required = false, HelpText = "Disables GraphQL endpoint for all entities.")]
        public bool GraphQLDisabled { get; }

        public void Handler(ILogger logger, RuntimeConfigLoader loader, IFileSystem fileSystem)
        {
            logger.LogInformation($"{PRODUCT_NAME} {ProductInfo.GetProductVersion()}");
            bool isSuccess = ConfigGenerator.TryGenerateConfig(this, loader, fileSystem);
            if (isSuccess)
            {
                logger.LogInformation($"Config file generated.");
                logger.LogInformation($"SUGGESTION: Use 'dab add [entity-name] [options]' to add new entities in your config.");
            }
            else
            {
                logger.LogError($"Could not generate config file.");
            }
        }
    }
}
