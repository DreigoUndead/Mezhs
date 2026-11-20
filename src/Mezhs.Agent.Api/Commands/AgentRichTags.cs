using Mezhs.Common.RichUi;
using Mezhs.RichUi;

namespace Mezhs.Agent.Commands;

// Agent owns protocol tags and the sole UI behavior override.
public static class AgentRichTags
{
    public static readonly RichTagRegistry Tags = new(
        UiRichTags.Tags.Definitions
            .Select(tag => tag.Name == "escape"
                ? tag with {
                    Handle = context => context.Discover(candidate =>
                        !candidate.AllowAsChild &&
                        candidate.Role is RichTagRole.Command or RichTagRole.Marker)
                }
                : tag)
            .Concat(Registry.All.Select(command => new RichTagDefinition(
                command.Name,
                command.Form == CommandForm.Marker ? RichTagRole.Marker : RichTagRole.Command,
                new HashSet<string>(StringComparer.Ordinal),
                SearchChildren: false,
                AllowAsChild: false))),
        UiRichTags.Tags.SvgText);
}
