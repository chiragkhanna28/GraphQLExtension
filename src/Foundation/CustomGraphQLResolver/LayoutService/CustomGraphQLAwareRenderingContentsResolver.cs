using GraphQL;
using GraphQL.Language.AST;
using Sitecore.Abstractions;
using Sitecore.Data;
using Sitecore.Data.Items;
using Sitecore.LayoutService.Configuration;
using Sitecore.LayoutService.GraphQL.LayoutService;
using Sitecore.Services.GraphQL.Abstractions;
using Sitecore.Services.GraphQL.Hosting;
using Sitecore.Services.GraphQL.Hosting.Configuration;
using Sitecore.Services.GraphQL.Hosting.Performance;
using Sitecore.Services.GraphQL.Hosting.QueryTransformation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using Sitecore;
using Sitecore.LayoutService.GraphQL.Helpers;

namespace CustomGraphQLResolver.LayoutService
{
    public class CustomGraphQLAwareRenderingContentsResolver : GraphQLAwareRenderingContentsResolver
    {
        private static readonly ID JsonRenderingGraphQlQuery = ID.Parse("{17BB046A-A32A-41B3-8315-81217947611B}");
        private readonly IDocumentWriter _documentWriter;
        private readonly BaseLog _log;
        private readonly IAsyncHelpers _asyncHelpers;
        private readonly Dictionary<string, IGraphQLEndpoint> _graphQLEndpoints;
        private readonly IGraphQLEndpointManager _graphQlEndpointManager;

        public CustomGraphQLAwareRenderingContentsResolver(IGraphQLEndpointManager graphQLEndpointManager, IDocumentWriter documentWriter, BaseLog log, IAsyncHelpers asyncHelpers) : base( graphQLEndpointManager, documentWriter, log, asyncHelpers)
        {
            this._documentWriter = documentWriter;
            this._log = log;
            this._asyncHelpers = asyncHelpers;
            this._graphQlEndpointManager = graphQLEndpointManager;
        }

        public override object ResolveContents(Sitecore.Mvc.Presentation.Rendering rendering, IRenderingConfiguration renderingConfig)
        {
            RenderingItem renderingItem = rendering.RenderingItem;
            if (renderingItem == null)
                return base.ResolveContents(rendering, renderingConfig);
            string str = renderingItem.InnerItem[CustomGraphQLAwareRenderingContentsResolver.JsonRenderingGraphQlQuery];
            if (string.IsNullOrWhiteSpace(str))
                return base.ResolveContents(rendering, renderingConfig);
            IGraphQLEndpoint publicEndpoint = this.GetPublicEndpoint();
            if (publicEndpoint == null)
            {
                this._log.Error("Rendering " + renderingItem.InnerItem.Paths.FullPath + " defined a GraphQL query to resolve its data, but public GraphQL endpoint wasn't resolved. GraphQL resolution will not be used.", (object)this);
                return base.ResolveContents(rendering, renderingConfig);
            }
            GraphQLAwareRenderingContentsResolver.LocalGraphQLRequest localGraphQlRequest = new GraphQLAwareRenderingContentsResolver.LocalGraphQLRequest();
            localGraphQlRequest.Query = str;
            GraphQLAwareRenderingContentsResolver.LocalGraphQLRequest request = localGraphQlRequest;
            request.LocalVariables.Add("contextItem", (object)Context.Item.ID.Guid.ToString());
            request.LocalVariables.Add("datasource", (object)rendering.DataSource);
            request.LocalVariables.Add("language", (object)Context.Language.Name);
            Sitecore.Data.Fields.ReferenceField taxonomyItem = Context.Item.Fields["Taxonomy"];
            if (taxonomyItem != null)
            {
                request.LocalVariables.Add("taxonomy", taxonomyItem.TargetID.Guid.ToString());
            }
            IDocumentExecuter executor = publicEndpoint?.CreateDocumentExecutor();
            ExecutionOptions options = publicEndpoint?.CreateExecutionOptions((GraphQLRequest)request, !HttpContext.Current.IsCustomErrorEnabled);
            if (options == null)
                throw new ArgumentException("Endpoint returned null options.");
            TransformationResult transformationResult = publicEndpoint.SchemaInfo.QueryTransformer.Transform((GraphQLRequest)request);
            if (transformationResult.Errors != null)
                return (object)new ExecutionResult()
                {
                    Errors = transformationResult.Errors
                };
            options.Query = transformationResult.Document.OriginalQuery;
            options.Document = transformationResult.Document;
            if (options.Document.Operations.Any<Operation>((Func<Operation, bool>)(op => (uint)op.OperationType > 0U)))
                throw new InvalidOperationException("Cannot use mutations or subscriptions in a datasource query. Use queries only.");
            using (QueryTracer queryTracer = publicEndpoint.Performance.TrackQuery((GraphQLRequest)request, options))
            {
                ExecutionResult result = this._asyncHelpers.RunSyncWithThreadContext<ExecutionResult>((Func<Task<ExecutionResult>>)(() => executor.ExecuteAsync(options)));
                publicEndpoint.Performance.CollectMetrics(publicEndpoint.SchemaInfo.Schema, (IEnumerable<Operation>)options.Document.Operations, result);
                new QueryErrorLog((ILogger)new BaseLogAdapter(this._log)).RecordQueryErrors(result);
                queryTracer.Result = result;
                return (object)this._documentWriter.ToJObject((object)result);
            }
        }
        private IGraphQLEndpoint GetPublicEndpoint() => this._graphQlEndpointManager.GetEndpoints().FirstOrDefault<IGraphQLEndpoint>((Func<IGraphQLEndpoint, bool>)(e => e.Url.EndsWith("edge")));
    }
}