// dmon composition root — edit this file to customise your agent.
// Providers are declared here via #:package directives and wired with fluent verbs.
// Add tool extensions by declaring packages and wiring them via .AddToolExtension<T>().
//
// Example (add a tool extension):
//   #:package Acme.DmonExt@1.0.*
//   ...
//   DmonHost.CreateBuilder(args)
//       .UseAnthropic()
//       .UseOpenAI()
//       .UseGemini()
//       .UseOllama()
//       .UseMtplx()
//       .AddBuiltinTools()
//       .AddToolExtension<Acme.DmonExt.AcmeExtension>()
//       .Build()
//       .RunAsync();
#:package dmoncore@0.2.*
#:package Dmon.Providers.Anthropic@0.2.*
#:package Dmon.Providers.OpenAI@0.2.*
#:package Dmon.Providers.Gemini@0.2.*
#:package Dmon.Providers.Ollama@0.2.*
#:package Dmon.Providers.Mtplx@0.2.*
#:package Dmon.Tools.Builtin@0.2.*

using Dmon.Hosting;

await DmonHost.CreateBuilder(args)
    .UseAnthropic()
    .UseOpenAI()
    .UseGemini()
    .UseOllama()
    .UseMtplx()
    .AddBuiltinTools()
    .Build()
    .RunAsync();
