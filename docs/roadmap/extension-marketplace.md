# Extension Marketplace

**Status:** 💭 Idea  
**Depends on:** Extension model stable and in use, NuGet tier proven

---

## What

A discovery and distribution mechanism for dmon extensions — a way for users to find, install, and share extensions without manually managing NuGet feeds or dropping `.csx` files into directories.

## Why

The two-tier extension model (`.csx` + NuGet) gives developers a great authoring experience. But "find a package and install it" is still too much friction for users who just want the tool. A marketplace layer bridges that gap.

## Ideas for what it includes

- **Discovery** — browsable/searchable index of community extensions, probably backed by a curated list or a NuGet feed with a dmon tag.
- **Install from the agent** — `install <extension-name>` as a first-class command; agent resolves, downloads, and loads it.
- **Extension browser in Avalonia** — if/when the Avalonia host exists, a proper UI for browsing and managing extensions.
- **Ratings / trust model** — signed packages, community ratings, or a curated "verified" tier. Don't ship untrusted code silently.
- **Publish flow** — scaffolding to help a developer publish their promoted `.csx` extension to the marketplace.

## Notes

- Don't design this until the extension model is battle-tested. The right marketplace shape depends on how people actually use extensions.
- NuGet.org may be a sufficient backend — no need to run a custom feed server.
- Trust and sandboxing are the hard problems here. Extensions run with the permissions of the agent process.
- Explicitly out of scope for V1.
