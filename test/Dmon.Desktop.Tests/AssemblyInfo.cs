// Splat's Locator.Current and RxApp's global scheduler are process-global singletons.
// BuildDesktopServiceProvider() swaps the resolver mid-call (empty resolver installed,
// then repopulated), and ReactiveObject/WhenAnyValue paths resolve ICreatesObservableForProperty
// from that same global resolver. Parallel class execution races these two operations and
// produces "Could not find ICreatesObservableForProperty" failures.
// Any future test that calls BuildDesktopServiceProvider() or constructs a ReactiveObject
// depends on this serialisation — do not re-enable parallelization without isolating
// all global-Locator and RxApp usage into a sequential collection.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
