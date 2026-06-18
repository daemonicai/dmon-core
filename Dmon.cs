// dmon composition root — edit this file to customise your agent.
// Add extensions by declaring packages and wiring them via .AddToolExtension<T>().
//
// Example (add an extension):
//   #:package Acme.DmonExt@1.0.*
//   ...
//   DmonHost.CreateBuilder(args)
//       .AddBuiltinTools()
//       .AddToolExtension<Acme.DmonExt.AcmeExtension>()
//       .Build()
//       .RunAsync();
#:package dmoncore@0.2.*
#:package Dmon.Tools.Builtin@0.2.*

using Dmon.Hosting;

await DmonHost.CreateBuilder(args)
    .AddBuiltinTools()
    .Build()
    .RunAsync();