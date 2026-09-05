using Moq;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.ReplyMarkups;

namespace ProductTrackerBot.Tests.Integration;

[Collection("IntegrationTests")]
public class CommandIntegrationTests : TelegramIntegrationTestBase
{
    [Fact]
    public async Task Start_Sends_Welcome_Message()
    {
        await ClearDataAsync();

        await DispatchAsync(CommandUpdate(-100, 42, "/start"));

        BotMock.Verify(
            b => b.SendRequest(
                It.Is<SendMessageRequest>(r => !string.IsNullOrEmpty(r.Text)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Buy_With_Args_Sends_Review_Then_Adds_Item_On_Confirm()
    {
        await ClearDataAsync();

        // /buy with inline args sends a review message (no immediate DB add)
        await DispatchAsync(CommandUpdate(-100, 42, "/buy Milk 2l"));

        var confirmData = GetLastBuyConfirmCallbackData();
        Assert.NotNull(confirmData);

        // Item not added yet
        var group = await GroupRepository.GetOrCreateAsync(-100);
        Assert.Empty(await ItemRepository.GetAllAsync(group.Id));

        // Confirm the add
        BotMock.Invocations.Clear();
        await DispatchAsync(CallbackUpdate(-100, 42, 1, confirmData));

        BotMock.Verify(
            b => b.SendRequest(
                It.Is<SendMessageRequest>(r => r.Text.Contains("Milk")),
                It.IsAny<CancellationToken>()),
            Times.Once);

        var items = await ItemRepository.GetAllAsync(group.Id);
        Assert.Contains(items, i => i.Name == "Milk");
    }

    [Fact]
    public async Task Buy_Creates_Group_On_First_Use()
    {
        await ClearDataAsync();

        const long freshChatId = -88888L;
        await DispatchAsync(CommandUpdate(freshChatId, 42, "/buy FirstItem"));

        // Group is created on /buy, and the no-quantity item is saved immediately (no review step)
        var group = await GroupRepository.GetOrCreateAsync(freshChatId);
        Assert.True(group.Id > 0);

        var items = await ItemRepository.GetAllAsync(group.Id);
        Assert.NotEmpty(items);
    }

    [Fact]
    public async Task Buy_Without_Quantity_Adds_Immediately_No_Review_Message()
    {
        await ClearDataAsync();

        await DispatchAsync(CommandUpdate(-100, 42, "/buy Хлеб"));

        // Item is saved immediately
        var group = await GroupRepository.GetOrCreateAsync(-100);
        var items = await ItemRepository.GetAllAsync(group.Id);
        Assert.Contains(items, i => i.Name == "Хлеб");

        // No Confirm/Edit/Cancel review message was sent
        Assert.Null(GetLastBuyConfirmCallbackData());

        // Tag-capture prompt followed (no purchase history yet, so only the skip/done buttons — still an
        // inline-keyboard message distinct from the plain "added" confirmation).
        BotMock.Verify(
            b => b.SendRequest(
                It.Is<SendMessageRequest>(r => r.Text.Contains("tag.prompt")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Buy_Without_Quantity_Then_Stale_Confirm_Callback_Does_Not_Double_Add_Or_Error()
    {
        await ClearDataAsync();

        // A prior review session exists (quantity found) but is never confirmed.
        await DispatchAsync(CommandUpdate(-100, 42, "/buy Old 1kg"));
        var staleConfirmData = GetLastBuyConfirmCallbackData();
        Assert.NotNull(staleConfirmData);

        // Now a no-quantity /buy adds immediately.
        await DispatchAsync(CommandUpdate(-100, 42, "/buy Хлеб"));

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var itemsAfterDirectAdd = await ItemRepository.GetAllAsync(group.Id);
        Assert.Single(itemsAfterDirectAdd, i => i.Name == "Хлеб");

        // Tapping the stale review callback from the earlier "Old 1kg" session still works in isolation
        // and does not affect or duplicate the already-added "Хлеб" item.
        await DispatchAsync(CallbackUpdate(-100, 42, 1, staleConfirmData));

        var itemsAfterStaleConfirm = await ItemRepository.GetAllAsync(group.Id);
        Assert.Single(itemsAfterStaleConfirm, i => i.Name == "Хлеб");
        Assert.Single(itemsAfterStaleConfirm, i => i.Name == "Old");
    }

    [Fact]
    public async Task List_Shows_Empty_List_For_New_Group()
    {
        await ClearDataAsync();

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));

        BotMock.Verify(
            b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task List_Shows_Items_After_Buy_And_Confirm()
    {
        await ClearDataAsync();

        await DispatchAsync(CommandUpdate(-100, 42, "/buy Eggs"));

        BotMock.Invocations.Clear();

        await DispatchAsync(CommandUpdate(-100, 42, "/list"));

        BotMock.Verify(
            b => b.SendRequest(
                It.Is<SendMessageRequest>(r => r.Text.Contains("Eggs")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task List_Item_Row_Is_A_Single_Select_Button_Until_Tapped()
    {
        await ClearDataAsync();

        await DispatchAsync(CommandUpdate(-100, 42, "/buy Eggs"));

        BotMock.Invocations.Clear();
        await DispatchAsync(CommandUpdate(-100, 42, "/list"));

        var sent = GetLastSentMessage();
        Assert.NotNull(sent);
        var keyboard = Assert.IsType<InlineKeyboardMarkup>(sent!.ReplyMarkup);
        var row = keyboard.InlineKeyboard.First();

        var button = Assert.Single(row);
        Assert.StartsWith("shop:sel:", button.CallbackData);
        Assert.DoesNotContain(keyboard.InlineKeyboard.SelectMany(r => r), btn => btn.CallbackData?.StartsWith("shop:done:") == true);
        Assert.DoesNotContain(keyboard.InlineKeyboard.SelectMany(r => r), btn => btn.CallbackData?.StartsWith("shop:remove:") == true);
    }
}
