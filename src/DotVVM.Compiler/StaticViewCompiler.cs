using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using DotVVM.Framework.Compilation;
using DotVVM.Framework.Compilation.ControlTree;
using DotVVM.Framework.Compilation.ControlTree.Resolved;
using DotVVM.Framework.Compilation.Parser.Dothtml.Parser;
using DotVVM.Framework.Compilation.Parser.Dothtml.Tokenizer;
using DotVVM.Framework.Compilation.Styles;
using DotVVM.Framework.Compilation.Validation;
using DotVVM.Framework.Configuration;
using DotVVM.Framework.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace DotVVM.Compiler
{
    public class StaticViewCompiler
    {
        public const string ObjectsClassName = "SerializedObjects";

        private readonly DotvvmConfiguration configuration;

        // NB: Currently, an Assembly must be built for each view/markup control (i.e. IControlBuilder) and then merged
        //     into one assembly. It's horrible, I know, but the compiler is riddled with references to System.Type
        //     that make it presently impossible to compile it all in one go.
        private readonly ConcurrentDictionary<string, StaticView> viewCache
            = new ConcurrentDictionary<string, StaticView>();

        public StaticViewCompiler(DotvvmConfiguration configuration)
        {
            this.configuration = configuration;
        }

        public StaticView GetView(string viewPath)
        {
            if (viewCache.ContainsKey(viewPath))
            {
                return viewCache[viewPath];
            }

            var view = CompileView(viewPath);
            // NB: in the meantime, the view could have been compiled on another thread
            return viewCache.GetOrAdd(viewPath, view);
        }

        public IEnumerable<StaticView> Compile(string assemblyName, Stream stream)
        {
            var markupControls = CompileViews(configuration.Markup.Controls.Select(c => c.Src));
            var views = CompileViews(configuration.RouteTable.Select(r => r.VirtualPath));

            var viewCompiler = configuration.ServiceProvider.GetRequiredService<IViewCompiler>();
            var compilation = viewCompiler.CreateCompilation(assemblyName);
            return markupControls.Concat(views).ToImmutableArray();
        }

        private ImmutableArray<StaticView> CompileViews(IEnumerable<string> paths)
        {
            return paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => GetView(p))
                .ToImmutableArray();
        }

        private StaticView CompileView(string viewPath)
        {
            var fileLoader = configuration.ServiceProvider.GetRequiredService<IMarkupFileLoader>();
            var file = fileLoader.GetMarkup(configuration, viewPath);
            if (file is null)
            {
                throw new ArgumentException($"No view with path '{viewPath}' exists.");
            }

            // parse the document
            var tokenizer = new DothtmlTokenizer();
            tokenizer.Tokenize(file.ContentsReaderFactory());
            var parser = new DothtmlParser();
            var node = parser.Parse(tokenizer.Tokens);

            // analyze control types
            var controlTreeResolver = configuration.ServiceProvider.GetRequiredService<IControlTreeResolver>();
            var resolvedView = (ResolvedTreeRoot)controlTreeResolver.ResolveTree(node, viewPath);

            var view = new StaticView(viewPath);
            var reports = new List<Report>();

            try
            {
                var errorCheckingVisitor = new ErrorCheckingVisitor();
                resolvedView.Accept(errorCheckingVisitor);
            }
            catch(DotvvmCompilationException e)
            {
                reports.Add(new Report(viewPath, e));
                // the error is too severe for compilation to continue 
                return view.WithReports(reports);
            }

            foreach (var n in node.EnumerateNodes())
            {
                if (n.HasNodeErrors)
                {
                    var line = n.Tokens.FirstOrDefault()?.LineNumber ?? -1;
                    var column = n.Tokens.FirstOrDefault()?.ColumnNumber ?? -1;

                    reports.AddRange(n.NodeErrors.Select(e => new Report(viewPath, line, column, e)));
                    // these errors are once again too severe
                    return view.WithReports(reports);
                }
            }

            var contextSpaceVisitor = new DataContextPropertyAssigningVisitor();
            resolvedView.Accept(contextSpaceVisitor);

            var styleVisitor = new StylingVisitor(configuration);
            resolvedView.Accept(styleVisitor);

            var usageValidator = configuration.ServiceProvider.GetRequiredService<IControlUsageValidator>();
            var validationVisitor = new ControlUsageValidationVisitor(usageValidator);
            resolvedView.Accept(validationVisitor);
            foreach(var error in validationVisitor.Errors)
            {
                var line = error.Nodes.FirstOrDefault()?.Tokens?.FirstOrDefault()?.LineNumber ?? -1;
                var column = error.Nodes.FirstOrDefault()?.Tokens?.FirstOrDefault().ColumnNumber ?? -1;

                reports.Add(new Report(viewPath, line, column, error.ErrorMessage));
            }

            if (reports.Any())
            {
                return view.WithReports(reports);
            }

            // no dothtml compilation errors beyond this point

            // NOTE: Markup controls referenced in the view have already been compiled "thanks" to the circular
            //       dependency in StaticViewControlResolver.

            var namespaceName = DefaultControlBuilderFactory.GetNamespaceFromFileName(
                file.FileName,
                file.LastWriteDateTimeUtc);
            var className = DefaultControlBuilderFactory.GetClassFromFileName(file.FileName) + "ControlBuilder";
            string fullClassName = namespaceName + "." + className;
            var refObjectSerializer = configuration.ServiceProvider.GetRequiredService<RefObjectSerializer>();
            var emitter = new CompileTimeCodeEmitter(refObjectSerializer, ObjectsClassName);
            var bindingCompiler = configuration.ServiceProvider.GetRequiredService<IBindingCompiler>();
            var compilingVisitor = new ViewCompilingVisitor(emitter, bindingCompiler, className);
            resolvedView.Accept(compilingVisitor);
            if (resolvedView.Directives.ContainsKey("masterPage"))
            {
                // make sure that the masterpage chain is already compiled
                _ = GetView(resolvedView.Directives["masterPage"].Single().Value);
            }

            var syntaxTree = emitter.BuildTree(namespaceName, className, viewPath).Single();
            var references = emitter.UsedAssemblies.Select(a => MetadataReference.CreateFromFile(a.Key.Location));
            return view.WithSyntaxTree(syntaxTree).WithRequiredReferences(references);
        }
    }
}