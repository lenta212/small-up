using System.Linq;
using System.Text;
using Content.Server.Popups;
using Content.Shared._LuaM.Sector;
using Content.Shared.Paper;
using Content.Shared.Verbs;

namespace Content.Server._LuaM.Sector;

public sealed partial class LuaMSectorInsuranceTerminalSystem : EntitySystem
{
    [Dependency] private LuaMSectorStorySystem _stories = default!;
    [Dependency] private PaperSystem _paper = default!;
    [Dependency] private PopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMSectorInsuranceTerminalComponent, GetVerbsEvent<InteractionVerb>>(OnGetVerbs);
    }

    private void OnGetVerbs(EntityUid uid, LuaMSectorInsuranceTerminalComponent component, GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        args.Verbs.Add(new InteractionVerb
        {
            IconEntity = GetNetEntity(uid),
            Text = "print insurance docket",
            Priority = 1,
            Act = () => TryPrintInsuranceDocket(uid, args.User, out _, component),
        });

        args.Verbs.Add(new InteractionVerb
        {
            IconEntity = GetNetEntity(uid),
            Text = "print insurance claim voucher",
            Priority = 1,
            Act = () => TryPrintInsuranceClaimVoucher(uid, args.User, out _, component),
        });
    }

    public bool TryPrintInsuranceDocket(
        EntityUid uid,
        EntityUid user,
        out EntityUid report,
        LuaMSectorInsuranceTerminalComponent? component = null)
    {
        report = default;

        if (!Resolve(uid, ref component, false))
            return false;

        report = Spawn(component.PaperPrototype, Transform(uid).Coordinates);
        if (!TryComp<PaperComponent>(report, out var paper))
        {
            QueueDel(report);
            _popup.PopupEntity("Insurance docket printer failed.", uid, user);
            report = default;
            return false;
        }

        _paper.SetContent((report, paper), BuildInsuranceDocket());
        _popup.PopupEntity("Insurance docket printed.", uid, user);
        return true;
    }

    public bool TryPrintInsuranceClaimVoucher(
        EntityUid uid,
        EntityUid user,
        out EntityUid voucher,
        LuaMSectorInsuranceTerminalComponent? component = null)
    {
        voucher = default;

        if (!Resolve(uid, ref component, false))
            return false;

        if (!TryGetNextUnclaimedCase(out var story))
        {
            _popup.PopupEntity("No unclaimed LuaM insurance case is available.", uid, user);
            return false;
        }

        voucher = Spawn(component.PaperPrototype, Transform(uid).Coordinates);
        if (!TryComp<PaperComponent>(voucher, out var paper))
        {
            QueueDel(voucher);
            _popup.PopupEntity("Insurance claim voucher printer failed.", uid, user);
            voucher = default;
            return false;
        }

        var evidence = AddComp<LuaMSectorEvidenceComponent>(voucher);
        evidence.Story = story.Story;
        evidence.ClaimInsurance = true;
        evidence.Note = $"insurance claim voucher filed: {story.Title}";

        _paper.SetContent((voucher, paper), BuildInsuranceClaimVoucher(story));
        _popup.PopupEntity($"Insurance claim voucher printed: {story.Title}", uid, user);
        return true;
    }

    public bool TryPrintInsuranceClaimVoucherForStory(
        EntityUid uid,
        EntityUid user,
        out EntityUid voucher,
        string storyId,
        LuaMSectorInsuranceTerminalComponent? component = null)
    {
        voucher = default;

        if (!Resolve(uid, ref component, false))
            return false;

        if (!TryGetUnclaimedCase(storyId, out var story))
        {
            _popup.PopupEntity("Selected LuaM insurance case is not available for claim.", uid, user);
            return false;
        }

        voucher = Spawn(component.PaperPrototype, Transform(uid).Coordinates);
        if (!TryComp<PaperComponent>(voucher, out var paper))
        {
            QueueDel(voucher);
            _popup.PopupEntity("Insurance claim voucher printer failed.", uid, user);
            voucher = default;
            return false;
        }

        var evidence = AddComp<LuaMSectorEvidenceComponent>(voucher);
        evidence.Story = story.Story;
        evidence.ClaimInsurance = true;
        evidence.Note = $"insurance claim voucher filed: {story.Title}";

        _paper.SetContent((voucher, paper), BuildInsuranceClaimVoucher(story));
        _popup.PopupEntity($"Insurance claim voucher printed: {story.Title}", uid, user);
        return true;
    }

    public LuaMSectorInsuranceUiEntry[] BuildInsuranceUiEntries()
    {
        var payouts = _stories.GetInsurancePayouts()
            .ToDictionary(payout => payout.Story.ToString(), StringComparer.Ordinal);

        return _stories.GetInsuranceCases()
            .OrderBy(record => record.Story.ToString(), StringComparer.Ordinal)
            .Select(record =>
            {
                var storyId = record.Story.ToString();
                var claimed = payouts.TryGetValue(storyId, out var payout);
                return new LuaMSectorInsuranceUiEntry
                {
                    StoryId = storyId,
                    Title = record.Title,
                    Vessel = record.ContractVessel,
                    Policy = record.Insurance,
                    State = claimed
                        ? payout!.Paid ? $"paid {payout.Amount}" : $"unpaid {payout.Amount}"
                        : "unclaimed",
                    Claimed = claimed,
                    Paid = payout?.Paid ?? false,
                    RequestedAmount = record.ContractReward,
                    PaidAmount = payout?.Amount ?? 0,
                    ServiceLine = BuildReputationServiceLine(record.ReputationTarget),
                };
            })
            .ToArray();
    }

    private bool TryGetNextUnclaimedCase(out LuaMSectorStoryRecord story)
    {
        var claimed = _stories.GetInsurancePayouts()
            .Select(payout => payout.Story.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var found = _stories.GetInsuranceCases()
            .Where(record => !claimed.Contains(record.Story.ToString()))
            .OrderBy(record => record.Story.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();

        if (found == null)
        {
            story = default!;
            return false;
        }

        story = found;
        return true;
    }

    private bool TryGetUnclaimedCase(string storyId, out LuaMSectorStoryRecord story)
    {
        var requested = storyId.Trim();
        if (string.IsNullOrWhiteSpace(requested))
        {
            story = default!;
            return false;
        }

        var claimed = _stories.GetInsurancePayouts()
            .Select(payout => payout.Story.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var found = _stories.GetInsuranceCases()
            .FirstOrDefault(record => record.Story.ToString().Equals(requested, StringComparison.Ordinal));

        if (found == null || claimed.Contains(found.Story.ToString()))
        {
            story = default!;
            return false;
        }

        story = found;
        return true;
    }

    private string BuildInsuranceDocket()
    {
        var output = new StringBuilder();
        var payouts = _stories.GetInsurancePayouts()
            .ToDictionary(payout => payout.Story.ToString(), StringComparer.Ordinal);

        output.AppendLine("# LuaM salvage insurance docket");
        output.AppendLine();
        output.AppendLine("## Cases");

        var cases = _stories.GetInsuranceCases()
            .OrderBy(record => record.Story.ToString(), StringComparer.Ordinal)
            .ToList();
        if (cases.Count == 0)
        {
            output.AppendLine("- No insurance cases are currently loaded.");
        }
        else
        {
            foreach (var record in cases)
            {
                var state = payouts.TryGetValue(record.Story.ToString(), out var payout)
                    ? payout.Paid ? $"paid {payout.Amount}" : $"unpaid {payout.Amount}"
                    : "unclaimed";
                output.AppendLine($"- {record.Story}: {record.Title} ({state})");
                output.AppendLine($"  Policy: {record.Insurance}");
                output.AppendLine($"  {BuildReputationServiceLine(record.ReputationTarget)}");
            }
        }

        output.AppendLine();
        output.AppendLine("## Filing");
        output.AppendLine("Print a claim voucher, fill the field result, then file sector evidence from that voucher.");
        return output.ToString();
    }

    private string BuildInsuranceClaimVoucher(LuaMSectorStoryRecord story)
    {
        var output = new StringBuilder();
        output.AppendLine("# LuaM salvage insurance claim voucher");
        output.AppendLine();
        output.AppendLine($"Story: {story.Story}");
        output.AppendLine($"Claim: {story.Title}");
        output.AppendLine($"Vessel: {story.ContractVessel}");
        output.AppendLine($"Requested amount: {story.ContractReward}");
        output.AppendLine(BuildReputationServiceLine(story.ReputationTarget));
        output.AppendLine();
        output.AppendLine("## Policy");
        output.AppendLine(story.Insurance);
        output.AppendLine();
        output.AppendLine("## Field declaration");
        output.AppendLine("[ ] route or wreck location verified");
        output.AppendLine("[ ] cargo, tow, rescue, repair, or loss result recorded");
        output.AppendLine("[ ] fraud indicators checked");
        output.AppendLine("[ ] sector evidence filed from this voucher");
        output.AppendLine();
        output.AppendLine("## Hazard context");
        output.AppendLine(string.IsNullOrWhiteSpace(story.Hazard) ? "No hazard recorded." : story.Hazard);
        return output.ToString();
    }

    private string BuildReputationServiceLine(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return "Service tier: standard; reputation bonus: 0.";

        var status = _stories.GetStatusSnapshot().Reputation
            .FirstOrDefault(entry => entry.Target == target);

        if (status == null)
            return $"Service tier: standard {target} 0; reputation bonus: 0.";

        return $"Service tier: {status.Tier} {status.Target} {status.Value}; reputation bonus: {status.RewardBonus}.";
    }
}
