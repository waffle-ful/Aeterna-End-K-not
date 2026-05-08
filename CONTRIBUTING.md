# Contributing to End Knot

Thanks for your interest in contributing! End Knot is a small personal fork — contributions are welcome but the bar for being merged is "the maintainer agrees and has time to review".

## Getting in touch

- **Bug reports / feature requests**: open a GitHub Issue on this repository
- **Discussion / questions**: <TBD: Discord invite link>

You don't need to file an issue before opening a small PR. For larger changes (new roles, refactors, gamemode tweaks), please open an issue or ask on Discord first so we can confirm the change is wanted before you spend time on it.

## Workflow

- Single branch (`main`)
- Squash-merge PRs
- Linking PRs to issues is optional

## Coding guidelines

- Match the existing code style. JetBrains Rider is recommended; the editor settings (`EndKnot.sln.DotSettings`) are committed and will sync automatically.
- Follow upstream EHR's conventions for role/option/RPC IDs (see `CLAUDE.md` and `Modules/RPC.cs`).
- New user-facing strings need an `en_US.jsonc` entry in `Resources/Lang/`.

## License

By contributing, you agree your contribution is licensed under GPL-3.0, the same license as the rest of the project.
