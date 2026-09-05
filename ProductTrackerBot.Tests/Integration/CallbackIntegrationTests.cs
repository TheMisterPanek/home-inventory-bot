using Moq;
using Telegram.Bot.Requests;

namespace ProductTrackerBot.Tests.Integration;

[Collection("IntegrationTests")]
public class CallbackIntegrationTests : TelegramIntegrationTestBase
{
    [Fact]
    public async Task ShopDone_Marks_Item_Done_Without_Sending_Any_Reply()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var item = await ItemRepository.AddAsync(group.Id, "Milk", "2l", "TestUser");

        await DispatchAsync(CallbackUpdate(-100, 42, 99, $"shop:done:{item.Id}"));

        var remaining = await ItemRepository.GetAllAsync(group.Id);
        Assert.DoesNotContain(remaining, i => i.Id == item.Id);

        // Buying is silent: no confirmation and no follow-up prompt, only the list message is edited
        BotMock.Verify(
            b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ShopRemove_Removes_Item_And_Sends_Reply()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var item = await ItemRepository.AddAsync(group.Id, "Bread", null, "TestUser");

        await DispatchAsync(CallbackUpdate(-100, 42, 99, $"shop:remove:{item.Id}"));

        var remaining = await ItemRepository.GetAllAsync(group.Id);
        Assert.DoesNotContain(remaining, i => i.Id == item.Id);
    }

    [Fact]
    public async Task Unknown_Callback_Prefix_No_Handler_Called()
    {
        await ClearDataAsync();

        await DispatchAsync(CallbackUpdate(-100, 42, 99, "unknown:action:1"));

        BotMock.Verify(
            b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Legacy_ItemEdit_Callback_From_Stale_Keyboard_Is_A_NoOp()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        var item = await ItemRepository.AddAsync(group.Id, "Milk", "2l", "TestUser");

        await DispatchAsync(CallbackUpdate(-100, 42, 99, $"item:edit:{item.Id}"));

        BotMock.Verify(
            b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // No dialog opened: a subsequent free-text message is not swallowed as an edit reply.
        await DispatchAsync(MessageUpdate(-100, 42, "Milk 3l"));
        BotMock.Verify(
            b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var unchanged = await ItemRepository.GetAllAsync(group.Id);
        var milk = Assert.Single(unchanged, i => i.Id == item.Id);
        Assert.Equal("2l", milk.Quantity);
    }
}
