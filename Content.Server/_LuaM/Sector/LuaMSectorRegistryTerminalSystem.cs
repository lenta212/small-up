using System.Linq;
using System.Text;
using Content.Server.Popups;
using Content.Shared._LuaM.Sector;
using Content.Shared.Paper;
using Content.Shared.Verbs;

namespace Content.Server._LuaM.Sector;

public sealed partial class LuaMSectorRegistryTerminalSystem : EntitySystem
{
    [Dependency] private LuaMSectorStorySystem _stories = default!;
    [Dependency] private PaperSystem _paper = default!;
    [Dependency] private PopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMSectorRegistryTerminalComponent, GetVerbsEvent<InteractionVerb>>(OnGetVerbs);
    }

    private void OnGetVerbs(EntityUid uid, LuaMSectorRegistryTerminalComponent component, GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        args.Verbs.Add(new InteractionVerb
        {
            IconEntity = GetNetEntity(uid),
            Text = "print registry docket",
            Priority = 1,
            Act = () => TryPrintRegistryDocket(uid, args.User, out _, component),
        });

        args.Verbs.Add(new InteractionVerb
        {
            IconEntity = GetNetEntity(uid),
            Text = "print charter voucher",
            Priority = 1,
            Act = () => TryPrintCharterVoucher(uid, args.User, out _, component),
        });
    }

    public bool TryPrintRegistryDocket(
        EntityUid uid,
        EntityUid user,
        out EntityUid report,
        LuaMSectorRegistryTerminalComponent? component = null)
    {
        report = default;

        if (!Resolve(uid, ref component, false))
            return false;

        report = Spawn(component.PaperPrototype, Transform(uid).Coordinates);
        if (!TryComp<PaperComponent>(report, out var paper))
        {
            QueueDel(report);
            _popup.PopupEntity("Registry docket printer failed.", uid, user);
            report = default;
            return false;
        }

        _paper.SetContent((report, paper), BuildRegistryDocket());
        _popup.PopupEntity("Registry docket printed.", uid, user);
        return true;
    }

    public bool TryPrintCharterVoucher(
        EntityUid uid,
        EntityUid user,
        out EntityUid voucher,
        LuaMSectorRegistryTerminalComponent? component = null)
    {
        voucher = default;

        if (!Resolve(uid, ref component, false))
            return false;

        if (!TryGetNextUnregisteredRecord(out var story, out var registerCompany, out var registerShip))
        {
            _popup.PopupEntity("No unregistered LuaM company or ship record is available.", uid, user);
            return false;
        }

        voucher = Spawn(component.PaperPrototype, Transform(uid).Coordinates);
        if (!TryComp<PaperComponent>(voucher, out var paper))
        {
            QueueDel(voucher);
            _popup.PopupEntity("Charter voucher printer failed.", uid, user);
            voucher = default;
            return false;
        }

        var evidence = AddComp<LuaMSectorEvidenceComponent>(voucher);
        evidence.Story = story.Story;
        evidence.RegisterCompany = registerCompany;
        evidence.RegisterShip = registerShip;
        evidence.Note = $"registry charter voucher filed: {story.Title}";

        _paper.SetContent((voucher, paper), BuildCharterVoucher(story, registerCompany, registerShip));
        _popup.PopupEntity($"Charter voucher printed: {story.Title}", uid, user);
        return true;
    }

    public bool TryPrintCharterVoucherForStory(
        EntityUid uid,
        EntityUid user,
        out EntityUid voucher,
        string storyId,
        LuaMSectorRegistryTerminalComponent? component = null)
    {
        voucher = default;

        if (!Resolve(uid, ref component, false))
            return false;

        if (!TryGetUnregisteredRecord(storyId, out var story, out var registerCompany, out var registerShip))
        {
            _popup.PopupEntity("Selected LuaM registry record is not available for charter filing.", uid, user);
            return false;
        }

        voucher = Spawn(component.PaperPrototype, Transform(uid).Coordinates);
        if (!TryComp<PaperComponent>(voucher, out var paper))
        {
            QueueDel(voucher);
            _popup.PopupEntity("Charter voucher printer failed.", uid, user);
            voucher = default;
            return false;
        }

        var evidence = AddComp<LuaMSectorEvidenceComponent>(voucher);
        evidence.Story = story.Story;
        evidence.RegisterCompany = registerCompany;
        evidence.RegisterShip = registerShip;
        evidence.Note = $"registry charter voucher filed: {story.Title}";

        _paper.SetContent((voucher, paper), BuildCharterVoucher(story, registerCompany, registerShip));
        _popup.PopupEntity($"Charter voucher printed: {story.Title}", uid, user);
        return true;
    }

    public LuaMSectorRegistryUiEntry[] BuildRegistryUiEntries()
    {
        var companies = _stories.GetCompanyRegistry()
            .Select(entry => entry.Story.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var ships = _stories.GetShipRegistry()
            .Select(entry => entry.Story.ToString())
            .ToHashSet(StringComparer.Ordinal);

        return _stories.GetCompanyRecords()
            .Concat(_stories.GetShipRecords())
            .GroupBy(record => record.Story.ToString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(record => record.Story.ToString(), StringComparer.Ordinal)
            .Select(record =>
            {
                var storyId = record.Story.ToString();
                var canRegisterCompany = !string.IsNullOrWhiteSpace(record.CompanyRecord) &&
                                         !companies.Contains(storyId);
                var canRegisterShip = !string.IsNullOrWhiteSpace(record.ShipRecord) &&
                                      !ships.Contains(storyId);

                return new LuaMSectorRegistryUiEntry
                {
                    StoryId = storyId,
                    Title = record.Title,
                    Vessel = record.ContractVessel,
                    CompanyState = string.IsNullOrWhiteSpace(record.CompanyRecord)
                        ? "not required"
                        : canRegisterCompany ? "open" : "filed",
                    ShipState = string.IsNullOrWhiteSpace(record.ShipRecord)
                        ? "not required"
                        : canRegisterShip ? "open" : "filed",
                    CanRegisterCompany = canRegisterCompany,
                    CanRegisterShip = canRegisterShip,
                    ServiceLine = BuildReputationServiceLine(record.ReputationTarget),
                    CompanyRecord = record.CompanyRecord,
                    ShipRecord = record.ShipRecord,
                };
            })
            .ToArray();
    }

    private bool TryGetNextUnregisteredRecord(
        out LuaMSectorStoryRecord story,
        out bool registerCompany,
        out bool registerShip)
    {
        var companies = _stories.GetCompanyRegistry()
            .Select(entry => entry.Story.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var ships = _stories.GetShipRegistry()
            .Select(entry => entry.Story.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var records = _stories.GetCompanyRecords()
            .Concat(_stories.GetShipRecords())
            .GroupBy(record => record.Story.ToString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(record => record.Story.ToString(), StringComparer.Ordinal);

        foreach (var record in records)
        {
            registerCompany = !string.IsNullOrWhiteSpace(record.CompanyRecord) &&
                              !companies.Contains(record.Story.ToString());
            registerShip = !string.IsNullOrWhiteSpace(record.ShipRecord) &&
                           !ships.Contains(record.Story.ToString());

            if (!registerCompany && !registerShip)
                continue;

            story = record;
            return true;
        }

        story = default!;
        registerCompany = false;
        registerShip = false;
        return false;
    }

    private bool TryGetUnregisteredRecord(
        string storyId,
        out LuaMSectorStoryRecord story,
        out bool registerCompany,
        out bool registerShip)
    {
        var requested = storyId.Trim();
        if (string.IsNullOrWhiteSpace(requested))
        {
            story = default!;
            registerCompany = false;
            registerShip = false;
            return false;
        }

        var companies = _stories.GetCompanyRegistry()
            .Select(entry => entry.Story.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var ships = _stories.GetShipRegistry()
            .Select(entry => entry.Story.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var record = _stories.GetCompanyRecords()
            .Concat(_stories.GetShipRecords())
            .GroupBy(candidate => candidate.Story.ToString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .FirstOrDefault(candidate => candidate.Story.ToString().Equals(requested, StringComparison.Ordinal));

        if (record == null)
        {
            story = default!;
            registerCompany = false;
            registerShip = false;
            return false;
        }

        registerCompany = !string.IsNullOrWhiteSpace(record.CompanyRecord) &&
                          !companies.Contains(record.Story.ToString());
        registerShip = !string.IsNullOrWhiteSpace(record.ShipRecord) &&
                       !ships.Contains(record.Story.ToString());

        if (!registerCompany && !registerShip)
        {
            story = default!;
            return false;
        }

        story = record;
        return true;
    }

    private string BuildRegistryDocket()
    {
        var output = new StringBuilder();
        var companies = _stories.GetCompanyRegistry()
            .Select(entry => entry.Story.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var ships = _stories.GetShipRegistry()
            .Select(entry => entry.Story.ToString())
            .ToHashSet(StringComparer.Ordinal);

        output.AppendLine("# LuaM company and ship registry docket");
        output.AppendLine();
        output.AppendLine("## Records");

        var records = _stories.GetCompanyRecords()
            .Concat(_stories.GetShipRecords())
            .GroupBy(record => record.Story.ToString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(record => record.Story.ToString(), StringComparer.Ordinal)
            .ToList();
        if (records.Count == 0)
        {
            output.AppendLine("- No registry records are currently loaded.");
        }
        else
        {
            foreach (var record in records)
            {
                var companyState = string.IsNullOrWhiteSpace(record.CompanyRecord)
                    ? "not required"
                    : companies.Contains(record.Story.ToString()) ? "filed" : "open";
                var shipState = string.IsNullOrWhiteSpace(record.ShipRecord)
                    ? "not required"
                    : ships.Contains(record.Story.ToString()) ? "filed" : "open";
                output.AppendLine($"- {record.Story}: {record.Title} (company {companyState}, ship {shipState})");
                output.AppendLine($"  {BuildReputationServiceLine(record.ReputationTarget)}");
            }
        }

        output.AppendLine();
        output.AppendLine("## Filing");
        output.AppendLine("Print a charter voucher, fill the registry fields, then file sector evidence from that voucher.");
        return output.ToString();
    }

    private string BuildCharterVoucher(
        LuaMSectorStoryRecord story,
        bool registerCompany,
        bool registerShip)
    {
        var output = new StringBuilder();
        output.AppendLine("# LuaM company and ship charter voucher");
        output.AppendLine();
        output.AppendLine($"Story: {story.Story}");
        output.AppendLine($"Lead: {story.Title}");
        output.AppendLine($"Vessel: {story.ContractVessel}");
        output.AppendLine($"Register company: {registerCompany}");
        output.AppendLine($"Register ship: {registerShip}");
        output.AppendLine(BuildReputationServiceLine(story.ReputationTarget));
        output.AppendLine();
        output.AppendLine("## Company record");
        output.AppendLine(string.IsNullOrWhiteSpace(story.CompanyRecord) ? "No company record required." : story.CompanyRecord);
        output.AppendLine();
        output.AppendLine("## Ship record");
        output.AppendLine(string.IsNullOrWhiteSpace(story.ShipRecord) ? "No ship record required." : story.ShipRecord);
        output.AppendLine();
        output.AppendLine("## Charter fields");
        output.AppendLine("[ ] operator or company name recorded");
        output.AppendLine("[ ] responding vessel recorded");
        output.AppendLine("[ ] claim, salvage, courier, or station-record context attached");
        output.AppendLine("[ ] sector evidence filed from this voucher");
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
