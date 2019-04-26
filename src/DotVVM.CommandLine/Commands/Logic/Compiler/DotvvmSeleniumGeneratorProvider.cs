﻿using System;
using System.Linq;
using DotVVM.Utils.ProjectService;
using DotVVM.Utils.ProjectService.Lookup;
using DotVVM.Utils.ProjectService.Operations.Providers;

namespace DotVVM.CommandLine.Commands.Logic.Compiler
{
    public class DotvvmSeleniumGeneratorProvider : DotvvmToolProvider
    {
        public static DotvvmToolMetadata GetToolMetadata(IResolvedProjectMetadata metadata)
        {


            var dotvvm = metadata.DotvvmProjectDependencies.First(s => s.Name.Equals("DotVVM", StringComparison.OrdinalIgnoreCase));
            if (dotvvm.IsProjectReference)
            {
                if ((metadata.TargetFramework & TargetFramework.NetFramework) > 0)
                {
                    return new DotvvmToolMetadata() {
                        MainModulePath =
                            CombineDotvvmRepositoryRoot(
                                    metadata,
                                    dotvvm,
                                    @"..\..\src\DotVVM.Framework.Tools.SeleniumGenerator\bin\Debug\net461\DotVVM.Framework.Tools.SeleniumGenerator.exe") ??
                                CombineDotvvmRepositoryRoot(
                                    metadata,
                                    dotvvm,
                                    @"DotVVM.Framework.Tools.SeleniumGenerator\bin\Debug\net461\DotVVM.Framework.Tools.SeleniumGenerator.exe"),
                        Version = DotvvmToolExecutableVersion.FullFramework
                    };
                }

                return new DotvvmToolMetadata() {
                    MainModulePath =
                        CombineDotvvmRepositoryRoot(metadata,
                            dotvvm,
                            @"..\..\src\DotVVM.Framework.Tools.SeleniumGenerator\bin\Debug\netcoreapp2.0\DotVVM.Framework.Tools.SeleniumGenerator.dll") ??
                        CombineDotvvmRepositoryRoot(
                            metadata,
                            dotvvm,
                            @"DotVVM.Framework.Tools.SeleniumGenerator\bin\Debug\netcoreapp2.0\DotVVM.Framework.Tools.SeleniumGenerator.dll"),
                    Version = DotvvmToolExecutableVersion.DotNetCore
                };
            }


            if ((metadata.TargetFramework & TargetFramework.NetFramework) > 0)
            {
                return new DotvvmToolMetadata() {
                    MainModulePath = CombineNugetPath(metadata, "tools\\selenium\\net46\\DotVVM.Framework.Tools.SeleniumGenerator.exe"),
                    Version = DotvvmToolExecutableVersion.FullFramework
                };
            }

            return new DotvvmToolMetadata() {
                MainModulePath = CombineNugetPath(metadata, "tools\\selenium\\netcoreapp2.0\\DotVVM.Framework.Tools.SeleniumGenerator.dll"),
                Version = DotvvmToolExecutableVersion.DotNetCore
            };
        }
    }
}
