// dmon composed core — demonstrates compile-time extension composition.
// The extension is a compile-time #:package dependency; no runtime load step.
// Build: dotnet build samples/Dmon.ComposedCore/Dmon.cs
// Run:   dotnet run --no-build samples/Dmon.ComposedCore/Dmon.cs
#:package dmoncore@0.2.*
#:package Dmon.SampleExtension@0.2.*

using Dmon.Hosting;
using Dmon.SampleExtension;

await DmonHost.CreateBuilder(args)
    .AddExtension<GreetingExtension>()
    .Build()
    .RunAsync();
