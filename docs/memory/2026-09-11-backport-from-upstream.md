# Backporting upstream's fixes onto 4.0.13

**2026-09-11.** Upstream is `JoelHauser/SPT-Casino`, now on **1.2.61 for the 4.1.x line**.
We are 1.3.0 on **4.0.13**. Both numbers are real; they are not the same product.

## Commits do not transfer. Content does.

`git merge-base HEAD upstream/main` is **the first commit in the repo**. Upstream rewrote
history when it merged the three original mods in, so every commit we appear to share has
a different hash and `git log HEAD..upstream/main` reports 126 commits we "lack" of which
most we already have.

So: add the remote, diff the **files**, and port the logic. Never cherry-pick.

```
git remote add upstream https://github.com/JoelHauser/SPT-Casino.git
git fetch upstream
git show upstream/main:src/Casino.Shared/ProfileSync.cs > /tmp/up.cs && diff -u ours /tmp/up.cs
```

## Their code does not compile here, and that is normal

Upstream's `ProfileSync` declares `private static IClientSession MainAppSession()`.
**`IClientSession` does not exist on 4.0.13's EFT.** Taking the file wholesale fails with
CS0246 on that one line.

The fix that worked is worth reusing: **let the compiler infer the type instead of naming
it.**

```csharp
var session = OrMainApp(ItemUiContext.Instance?.ClientSession);

private static T OrMainApp<T>(T current) where T : class { ... }
```

T comes from the property, so the type is never written down. That is strictly better than
upstream's version for the reason upstream's own notes give: `GetClientBackEndSession`
returns a class the obfuscator renames, and naming it puts a typeref in the assembly that
resolves only against the exact `Assembly-CSharp.dll` it was built on.

## What was taken, and why

| upstream | what it fixes | ours |
| --- | --- | --- |
| `fd505e4` | **Bank drew a stake from wherever it found currency first** -- pockets, Gamma, rig, not just the stash | taken, + 8 tests |
| `9dfa5ee` / `c792f96` | **Stash never updated** if you entered from the task bar without opening a stash first -- no `ItemUiContext`, silent return | taken, adapted |
| `d3ec9b1` | A dead reel wedged SPIN on "..." for the session; a spin that ended badly never settled | 2 of 3 taken |
| `a8b4762` (part) | `FetchAll` broke the whole pass on the first symbol it could not draw | taken |

**Not taken:**

- `b4047fe` fixes the build against **EFT 0.16.9.5 build 40743**, newer than our
  0.16.9.40087. Applying it would break us, not help us.
- `a8b4762`'s main body resolves `ItemFactory` by member shape because the obfuscator
  emptied its declaring type's name on *their* build. Our icons render, so our
  `ItemFactoryClass` name still resolves. Their lesson is still worth knowing --
  **verifying a metadata token on the machine you read it from cannot detect what is wrong
  with it** -- but the code is not ours to need yet.
- `808257e` gives every player a million roubles once. That is their release's apology,
  not a fix.
- Sound and deal animations are features, not fixes, and pull in `SoundBoard` which we do
  not have.

## The third of `d3ec9b1` we did not need

"The SPIN button goes back before anything that reads the reply" -- our `Settled` already
called `SetSpinEnabled(Ready)` first. Worth checking rather than assuming: two of the three
faults were ours and the third never was.
