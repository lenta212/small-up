#nullable enable

using Content.Server._NF.Bank;
using NUnit.Framework;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMBankDurableMutationContractTest
{
    [TestCase(10_000, 10_000, 7_500, "ConfirmedOriginal")]
    [TestCase(7_500, 10_000, 7_500, "ConfirmedMutation")]
    [TestCase(8_750, 10_000, 7_500, "Unknown")]
    [TestCase(null, 10_000, 7_500, "Unknown")]
    public void InitialProfileSaveFailureIsClassifiedFromFreshBalance(
        int? persistedBalance,
        int originalBalance,
        int mutatedBalance,
        string expected)
    {
        var outcome = BankSystem.ClassifyProfileSaveFailure(
            persistedBalance,
            originalBalance,
            mutatedBalance);

        Assert.That(outcome.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void OnlyConfirmedMutationMayContinueWorldFinalizer()
    {
        var confirmedCommit = BankSystem.ClassifyProfileSaveFailure(12_500, 10_000, 12_500);
        var confirmedOriginal = BankSystem.ClassifyProfileSaveFailure(10_000, 10_000, 12_500);
        var unknown = BankSystem.ClassifyProfileSaveFailure(11_000, 10_000, 12_500);

        Assert.Multiple(() =>
        {
            Assert.That(confirmedCommit, Is.EqualTo(BankSystem.ProfileSaveFailureOutcome.ConfirmedMutation));
            Assert.That(confirmedOriginal, Is.Not.EqualTo(BankSystem.ProfileSaveFailureOutcome.ConfirmedMutation));
            Assert.That(unknown, Is.Not.EqualTo(BankSystem.ProfileSaveFailureOutcome.ConfirmedMutation));
        });
    }
}
