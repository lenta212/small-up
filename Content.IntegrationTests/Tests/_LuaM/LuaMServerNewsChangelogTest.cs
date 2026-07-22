using System.Linq;
using Content.Client.Changelog;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMServerNewsChangelogTest
{
    [Test]
    public async Task ServerNewsIsPrimaryAndContainsPlayerFacingHistory()
    {
        await using var pair = await PoolManager.GetServerClient();
        var client = pair.Client;
        var changelogManager = client.ResolveDependency<ChangelogManager>();
        var resources = client.ResolveDependency<IResourceManager>();

        var changelogs = await changelogManager.LoadChangelog();
        var serverNews = changelogs.Single(changelog =>
            changelog.Name == ChangelogManager.MainChangelogName);

        await client.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(ChangelogManager.MainChangelogName, Is.EqualTo("ServerNews"));
                Assert.That(serverNews.AdminOnly, Is.False);
                Assert.That(serverNews.Order, Is.EqualTo(-100));
                Assert.That(changelogs.First(changelog => !changelog.AdminOnly), Is.SameAs(serverNews));
                Assert.That(serverNews.Entries, Has.Count.GreaterThanOrEqualTo(5));
                Assert.That(
                    resources.ContentFileExists(new ResPath("/Locale/ru-RU/_LuaM/changelog/server-news.ftl")),
                    Is.True);
                Assert.That(
                    resources.ContentFileExists(new ResPath("/Locale/en-US/_LuaM/changelog/server-news.ftl")),
                    Is.True);
            });

            var ids = serverNews.Entries.Select(entry => entry.Id).ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(ids, Is.Unique);
                Assert.That(ids, Is.Ordered.Ascending);
                Assert.That(ids, Does.Contain(2026071908));
                Assert.That(ids, Does.Contain(2026072005));
                Assert.That(ids, Does.Contain(2026072006));
                Assert.That(ids, Does.Contain(2026072007));
                Assert.That(ids, Does.Contain(2026072008));
                Assert.That(ids, Does.Contain(2026072010));
                Assert.That(ids, Does.Contain(2026072011));
                Assert.That(ids, Does.Contain(2026072012));
                Assert.That(ids, Does.Contain(2026072013));
                Assert.That(ids, Does.Contain(2026072014));
                Assert.That(ids, Does.Contain(2026072015));
                Assert.That(ids, Does.Contain(2026072101));
                Assert.That(ids, Does.Contain(2026072102));
                Assert.That(ids, Does.Contain(2026072103));
                Assert.That(ids, Does.Contain(2026072106));
                Assert.That(ids, Does.Contain(2026072107));
                Assert.That(ids, Does.Contain(2026072108));
                Assert.That(ids, Does.Contain(2026072109));
                Assert.That(ids, Does.Contain(2026072110));
                Assert.That(ids, Does.Contain(2026072111));
                Assert.That(ids, Does.Contain(2026072112));
                Assert.That(ids, Does.Contain(2026072113));
                Assert.That(ids, Does.Contain(2026072114));
                Assert.That(ids, Does.Contain(2026072115));
                Assert.That(ids, Does.Contain(2026072116));
                Assert.That(ids, Does.Contain(2026072117));
                Assert.That(ids, Does.Contain(2026072120));
                Assert.That(ids, Does.Contain(2026072122));
                Assert.That(ids[^1], Is.GreaterThanOrEqualTo(2026072122));
            });

            foreach (var entry in serverNews.Entries)
            {
                foreach (var change in entry.Changes)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(change.Message, Does.Not.Contain("\uFFFD"),
                            $"Server-news entry {entry.Id} contains a Unicode replacement character.");
                        Assert.That(change.Message, Does.Not.Contain("???"),
                            $"Server-news entry {entry.Id} contains a run of replacement question marks.");
                    });
                }

                Assert.Multiple(() =>
                {
                    Assert.That(entry.Author, Is.AnyOf("Команда сервера", "LuaM"));
                    Assert.That(entry.Time, Is.Not.EqualTo(default(DateTime)));
                    Assert.That(entry.Changes, Is.Not.Empty);
                    Assert.That(entry.Changes.All(change => !string.IsNullOrWhiteSpace(change.Message)), Is.True);
                });
            }

            var messages = string.Join('\n', serverNews.Entries
                .SelectMany(entry => entry.Changes)
                .Select(change => change.Message));
            Assert.Multiple(() =>
            {
                Assert.That(messages, Does.Contain("Новости сервера"));
                Assert.That(messages, Does.Contain("Обучение"));
                Assert.That(messages, Does.Contain("крио"));
                Assert.That(messages, Does.Contain("синтезаторы материи"));
                Assert.That(messages, Does.Contain("технологическая карта производства"));
                Assert.That(messages, Does.Contain("TSF Arrow"));
                Assert.That(messages, Does.Contain("IPC"));
                Assert.That(messages, Does.Contain("Горняк"));
                Assert.That(messages, Does.Contain("Саламандра"));
                Assert.That(messages, Does.Contain("медибота"));
                Assert.That(messages, Does.Contain("Очередь производства"));
                Assert.That(messages, Does.Contain("медицинский имплант"));
                Assert.That(messages, Does.Contain("Авангарда"));
                Assert.That(messages, Does.Contain("75 единиц"));
                Assert.That(messages, Does.Contain("Вульпканины"));
                Assert.That(messages, Does.Contain("раздельные крики"));
                Assert.That(messages, Does.Contain("TTS"));
                Assert.That(messages, Does.Contain("станционному ИИ"));
                Assert.That(messages, Does.Contain("позитронный мозг"));
                Assert.That(messages, Does.Contain("синтетические персонажи"));
                Assert.That(messages, Does.Contain("соединения со станцией"));
                Assert.That(messages, Does.Contain("ложной занятости выбранных ворот"));
                Assert.That(messages, Does.Contain("Залезть"));
                Assert.That(messages, Does.Contain("обычным взаимодействием"));
                Assert.That(messages, Does.Contain("между раундами и персонажами"));
                Assert.That(messages, Does.Contain("Выбранные ворота"));
                Assert.That(messages, Does.Contain("отвязать от ID-карты"));
                Assert.That(messages, Does.Contain("выбранной паре портов"));
                Assert.That(messages, Does.Contain("управляемый шаттл"));
                Assert.That(messages, Does.Contain("дублирующих статусов синхронизации БСС"));
                Assert.That(messages, Does.Contain("очередью переработчика руды"));
                Assert.That(messages, Does.Contain("прогресс производства"));
                Assert.That(messages, Does.Contain("полученные по ваучерам"));
                Assert.That(messages, Does.Contain("карта станции"));
                Assert.That(messages, Does.Contain("Гальцион"));
                Assert.That(messages, Does.Contain("принадлежностью PDV"));
                Assert.That(messages, Does.Contain("планетоиды теперь прибывают парами"));
                Assert.That(messages, Does.Contain("5 минут перерыва"));
                Assert.That(messages, Does.Contain("рецепты категории «Компоненты»"));
                Assert.That(messages, Does.Contain("малахит → медь"));
                Assert.That(messages, Does.Contain("новый футуристичный интерфейс"));
                Assert.That(messages, Does.Contain("эффектом старого кинескопа"));
                Assert.That(messages, Does.Contain("свободно меняет размер"));
                Assert.That(messages, Does.Contain("инженеры и медики-боты"));
                Assert.That(messages, Does.Contain("реально удерживаемые инструменты"));
                Assert.That(messages, Does.Contain("Такси, грузовые и развлекательные боты"));
                Assert.That(messages, Does.Contain("Многоразовые корабельные ваучеры"));
                Assert.That(messages, Does.Contain("криптовалютной дата-фермы"));
                Assert.That(messages, Does.Contain("выбранный режим полёта"));
                Assert.That(messages, Does.Contain("соединённой группе пристыкованных шаттлов"));
                Assert.That(messages, Does.Contain("переработчик пластика"));
                Assert.That(messages, Does.Contain("пласталевых стен"));
                Assert.That(messages, Does.Contain("Экспедиционная консоль"));
                Assert.That(messages, Does.Contain("локальные рыночные данные корабля"));
                Assert.That(messages, Does.Contain("локальной базой заказов корабля"));
                Assert.That(messages, Does.Contain("Грузовая консоль шаттла"));
                Assert.That(messages, Does.Contain("локальную базу контрактов"));
                Assert.That(messages, Does.Contain("Грузовой телепад"));
                Assert.That(messages, Does.Contain("рыночные условия станции или корабля"));
                Assert.That(messages, Does.Contain("R&D-консоли"));
                Assert.That(messages, Does.Contain("озвучка внутриигровой речи персонажей"));
                Assert.That(messages, Does.Contain("шлюзом синтеза речи"));
            });
        });

        await pair.CleanReturnAsync();
    }
}
