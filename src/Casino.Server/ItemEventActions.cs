using System.Text.Json;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json.Converters;

namespace Casino.Server;

/// <summary>
/// How a table's own item-event actions get past SPT's JSON layer on 4.0.13.
///
/// ## The trap this exists to avoid
///
/// 4.1 lets a router declare its payload type -- `ItemRouteAction&lt;T&gt;` carries a
/// `BodyType` and SPT binds it. **4.0.13 has no such thing.** Instead it registers one
/// global `BaseInteractionRequestDataConverter`, and that converter is a closed switch
/// over EFT's own action names with a *throwing* default:
///
///     Unhandled action type BlackjackSync, make sure the
///     BaseInteractionRequestDataConverter has the deserialization for this action handled.
///
/// The throw happens inside `JsonUtil.Deserialize`, called from `StaticRouter` while it
/// is still deserializing the `ItemEventRouterRequest` -- so it lands **before any
/// `ItemEventRouterDefinition` is consulted at all**. A router that tries to recover the
/// payload itself, out of `body.ExtensionData`, is therefore unreachable code: it never
/// runs, because the request never gets that far. That was tried, it compiled, and it
/// was dead. Verified by running the real 4.0.13 assembly: all ten of this mod's
/// actions threw, while EFT's own "Move" bound fine.
///
/// This applies to **every** action, including the payload-less `*Sync` ones. The
/// converter does not care that there is nothing to bind; it cares that it does not
/// recognise the name.
///
/// ## What 4.0.13 gives instead, which is better than the workaround
///
/// The converter has a public, static, process-wide extension point --
/// `RegisterModDataHandler(string action, Func&lt;string, BaseInteractionRequestData&gt;)` --
/// whose handler is handed the raw JSON of the action object. So a mod can keep its
/// typed records exactly as they are and simply say how to build one. No `ExtensionData`
/// archaeology, and the records are unchanged between the 4.0.13 and 4.1.x shapes.
///
/// Deserialization deliberately borrows `JsonUtil.JsonSerializerOptionsNoIndent` -- SPT's
/// own options -- rather than inventing a set here. Whatever SPT does about casing, this
/// does too, which is the only way the 4.0.13 port binds a body the same way 4.1.x did.
/// (Note the standing footgun either way: bodies are matched PascalCase, and a lowercase
/// key binds nothing and leaves every field at its default. That is how a 100,000 stake
/// arrives as 0 while looking like it bound.)
///
/// ## When to call it
///
/// From the table's `Startup.OnLoad`, **before** any early return. Registration is
/// static and process-wide, so it only has to happen once and it only has to happen
/// before the first request -- but `OnLoad` is the last moment anything is guaranteed to
/// run, and a table whose banner is switched off still has to bind its own bodies.
/// </summary>
public static class ItemEventActions
{
    /// <summary>
    /// Teaches SPT to build <typeparamref name="T"/> when it sees
    /// <paramref name="action"/> on the item-event endpoint.
    /// </summary>
    public static void Register<T>(string action)
        where T : BaseInteractionRequestData, new() =>
        BaseInteractionRequestDataConverter.RegisterModDataHandler(
            action,
            raw => JsonSerializer.Deserialize<T>(raw, JsonUtil.JsonSerializerOptionsNoIndent)
                   ?? new T());

    /// <summary>
    /// The typed body a registered handler already built, as the action it belongs to.
    ///
    /// This is a plain cast and it is meant to be. Once <see cref="Register{T}"/> has
    /// run, SPT hands the router the real instance, so a failure here means the
    /// registration and the dispatch disagree about a type -- a wiring mistake, at
    /// startup, in this mod. Falling back to `new T()` would turn that into a silent
    /// round played for a stake of zero, which is precisely the failure this whole file
    /// exists to keep loud.
    /// </summary>
    public static T Typed<T>(BaseInteractionRequestData body)
        where T : BaseInteractionRequestData =>
        body as T
        ?? throw new InvalidOperationException(
            $"item event '{body.Action}' arrived as {body.GetType().Name}, not {typeof(T).Name}. "
            + $"Its handler is not registered, or is registered against a different type -- "
            + $"see {nameof(ItemEventActions)}.{nameof(Register)}.");
}
