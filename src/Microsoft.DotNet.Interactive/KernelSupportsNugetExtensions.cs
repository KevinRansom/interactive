﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Rendering;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Html;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Formatting;
using static Microsoft.DotNet.Interactive.Formatting.PocketViewTags;

namespace Microsoft.DotNet.Interactive
{
    public static class KernelSupportsNugetExtensions
    {
        public static T UseNugetDirective<T>(this T kernel) 
            where T: Kernel, ISupportNuget
        {
            kernel.AddDirective(i());
            kernel.AddDirective(r());

            var restore = new Command("#!nuget-restore")
            {
                Handler = CommandHandler.Create(DoNugetRestore()),
                IsHidden = true
            };

            kernel.AddDirective(restore);

            return kernel;
        }

        private static Command i()
        {
            var iDirective = new Command("#i")
            {
                new Argument<string>("source")
            };
            iDirective.Handler = CommandHandler.Create<string, KernelInvocationContext>((source, context) =>
            {
                if (context.HandlingKernel is ISupportNuget kernel)
                {
                    kernel.AddRestoreSource(source.Replace("nuget:", ""));
                    IHtmlContent content = div(
                        strong("Restore sources"),
                        ul(kernel.RestoreSources.Select(s => li(span(s)))));

                    var displayed = new DisplayedValue("displayedValueRestoreSources" + context.GetHashCode().ToString(), HtmlFormatter.MimeType, context);
                    displayed.Update(content);
                }
            });
            return iDirective;
        }

        private static Command r()
        {
            var rDirective = new Command("#r")
            {
                new Argument<PackageReferenceOrFileInfo>(
                    result =>
                    {
                        var token = result.Tokens
                                          .Select(t => t.Value)
                                          .SingleOrDefault();

                        if (PackageReference.TryParse(token, out var reference))
                        {
                            return reference;
                        }

                        if (token != null &&
                            !token.StartsWith("nuget:") &&
                            !EndsInDirectorySeparator(token))
                        {
                            return new FileInfo(token);
                        }

                        result.ErrorMessage = $"Unable to parse package reference: \"{token}\"";

                        return null;
                    })
                {
                    Name = "package"
                }
            };

            rDirective.Handler = CommandHandler.Create<PackageReferenceOrFileInfo, KernelInvocationContext>(HandleAddPackageReference);

            return rDirective;

            Task HandleAddPackageReference(
                PackageReferenceOrFileInfo package,
                KernelInvocationContext context)
            {
                if (package?.Value is PackageReference pkg &&
                    context.HandlingKernel is ISupportNuget kernel)
                {
                    var alreadyGotten = kernel.ResolvedPackageReferences
                                              .Concat(kernel.RequestedPackageReferences)
                                              .FirstOrDefault(r => r.PackageName.Equals(pkg.PackageName, StringComparison.OrdinalIgnoreCase));

                    if (alreadyGotten is { } && !string.IsNullOrWhiteSpace(pkg.PackageVersion) && pkg.PackageVersion != alreadyGotten.PackageVersion)
                    {
                        var errorMessage = GenerateErrorMessage(pkg, alreadyGotten).ToString(OutputMode.NonAnsi);
                        context.Publish(new ErrorProduced(errorMessage, context.Command));
                    }
                    else
                    {
                        var added = kernel.GetOrAddPackageReference(pkg.PackageName, pkg.PackageVersion);

                        if (added is null)
                        {
                            var errorMessage = GenerateErrorMessage(pkg).ToString(OutputMode.NonAnsi);
                            context.Publish(new ErrorProduced(errorMessage, context.Command));
                        }
                    }

                    static TextSpan GenerateErrorMessage(
                        PackageReference requested,
                        PackageReference existing = null)
                    {
                        var spanFormatter = new TextSpanFormatter();
                        if (existing != null)
                        {
                            if (!string.IsNullOrEmpty(requested.PackageName))
                            {
                                if (!string.IsNullOrEmpty(requested.PackageVersion))
                                {
                                    return spanFormatter.ParseToSpan(
                                        $"{Ansi.Color.Foreground.Red}{requested.PackageName} version {requested.PackageVersion} cannot be added because version {existing.PackageVersion} was added previously.{Ansi.Text.AttributesOff}");
                                }
                            }
                        }

                        return spanFormatter.ParseToSpan($"Invalid Package specification: '{requested}'");
                    }
                }

                return Task.CompletedTask;
            }
        }

        private static bool EndsInDirectorySeparator(string path)
        {
            return path.Length > 0 && path.EndsWith(Path.DirectorySeparatorChar);
        }

        private class PackageReferenceComparer : IEqualityComparer<PackageReference>
        {
            public bool Equals(PackageReference x, PackageReference y) =>
                string.Equals(
                    GetDisplayValueId(x),
                    GetDisplayValueId(y),
                    StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(PackageReference obj) => obj.PackageName.ToLowerInvariant().GetHashCode();

            public static string GetDisplayValueId(PackageReference package)
            {
                return package.PackageName.ToLowerInvariant();
            }

            public static IEqualityComparer<PackageReference> Instance { get; } = new PackageReferenceComparer();
        }

        internal static KernelCommandInvocation DoNugetRestore()
        {
            return async (command, invocationContext) =>
            {
                KernelCommandInvocation restore = async (_, context) =>
                {
                    if (!(context.HandlingKernel is ISupportNuget kernel))
                    {
                        return;
                    }

                    var messages = new Dictionary<PackageReference, string>(new PackageReferenceComparer());
                    var displayedValues = new Dictionary<string, DisplayedValue>();

                    var newlyRequestedPackages =
                            kernel.RequestedPackageReferences
                                          .Except(kernel.ResolvedPackageReferences, PackageReferenceComparer.Instance)
                                          .ToArray();

                    var requestedPackageIds = new Dictionary<string, PackageReference>();

                    foreach (var package in newlyRequestedPackages)
                    {
                        var id = PackageReferenceComparer.GetDisplayValueId(package);
                        var message = InstallingPackageMessage(package) + "...";
                        var displayedValue = context.Display(message);
                        displayedValues[id] = displayedValue;
                        messages.Add(package, message);
                        requestedPackageIds.Add(id, package);
                    }

                    var restorePackagesTask = kernel.RestoreAsync();

                    var totalWaitMs = 0;
                    var delay = 500;
                    while (await Task.WhenAny(Task.Delay(delay), restorePackagesTask) != restorePackagesTask)
                    {
                        totalWaitMs += delay;
                        if (totalWaitMs > TimeSpan.FromMinutes(1.5).TotalMilliseconds)
                        {
                            throw new TimeoutException($"Package restore took longer than expected for packages: {string.Join(", ", newlyRequestedPackages.Select(p => p.PackageName))}.");
                        }

                        foreach (var package in messages.Keys.ToArray())
                        {
                            var id = PackageReferenceComparer.GetDisplayValueId(package);
                            if (displayedValues.TryGetValue(id, out var displayedValue))
                            {
                                requestedPackageIds.Remove(id);
                                var message = messages[package] + ".";
                                messages[package] = message;
                                displayedValue.Update(
                                    message);
                            }
                        }
                    }

                    var result = await restorePackagesTask;

                    if (result.Succeeded)
                    {
                        kernel?.RegisterResolvedPackageReferences(result.ResolvedReferences);

                        foreach (var resolvedReference in result.ResolvedReferences)
                        {
                            if (displayedValues.TryGetValue(
                                PackageReferenceComparer.GetDisplayValueId(resolvedReference), out var displayedValue))
                            {
                                displayedValue.Update(
                                    $"Installed package {resolvedReference.PackageName} version {resolvedReference.PackageVersion}");
                            }

                            context.Publish(new PackageAdded(resolvedReference, context.Command));
                        }

                        foreach (var package in requestedPackageIds.Values)
                        {
                            if (displayedValues.TryGetValue(
                                PackageReferenceComparer.GetDisplayValueId(package), out var displayedValue))
                            {
                                displayedValue.Update(
                                    $"Installed package {package.PackageName} version {package.PackageVersion}");
                            }
                        }
                    }
                    else
                    {
                        var errors = $"{string.Join(Environment.NewLine, result.Errors)}";

                        context.Fail(message: errors);
                    }
                };

                await invocationContext.HandlingKernel.SendAsync(new AnonymousKernelCommand(restore));
            };

            static string InstallingPackageMessage(PackageReference package)
            {
                string message = null;

                if (!string.IsNullOrEmpty(package.PackageName))
                {
                    message = $"Installing package {package.PackageName}";
                    if (!string.IsNullOrWhiteSpace(package.PackageVersion))
                    {
                        message += $", version {package.PackageVersion}";
                    }
                }

                return message;
            }
        }
    }
}
