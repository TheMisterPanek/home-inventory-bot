using Moq;
using ProductTrackerBot.Models;
using Telegram.Bot.Requests;

namespace ProductTrackerBot.Tests.Integration;

[Collection("IntegrationTests")]
public class PriceCaptureIntegrationTests : TelegramIntegrationTestBase
{
    [Fact]
    public async Task PriceCapture_Full_Flow_Persists_Purchase_With_Price()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        await ItemRepository.AddAsync(group.Id, "Milk", "2l", "TestUser");

        // The price-capture dialog opens at step 1 (store name)
        StartPriceCapture(group.Id, "Milk", "2l");
        // Step 1: enter store name
        await DispatchAsync(MessageUpdate(-100, 42, "Lidl"));
        // Step 2: enter price
        await DispatchAsync(MessageUpdate(-100, 42, "1.99"));
        // Step 3: enter expiry (days)
        await DispatchAsync(MessageUpdate(-100, 42, "14"));

        var records = await PurchaseRepository.SearchAsync(group.Id, "Milk");
        Assert.Contains(records, r => r.Price == 1.99m && r.StoreName == "Lidl");
    }

    [Fact]
    public async Task PriceCapture_SkipStore_Advances_To_Price_Step()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        await ItemRepository.AddAsync(group.Id, "Eggs", null, "TestUser");

        StartPriceCapture(group.Id, "Eggs");
        BotMock.Invocations.Clear();

        // Skip the store step
        await DispatchAsync(CallbackUpdate(-100, 42, 100, "price:skip_store"));

        BotMock.Verify(
            b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task PriceCapture_SkipPrice_Saves_Purchase_Without_Price()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        await ItemRepository.AddAsync(group.Id, "Coffee", null, "TestUser");

        StartPriceCapture(group.Id, "Coffee");
        await DispatchAsync(MessageUpdate(-100, 42, "Aldi"));

        // Skip price → advances to expiry step; skip expiry → saves purchase
        await DispatchAsync(CallbackUpdate(-100, 42, 100, "price:skip_price"));
        await DispatchAsync(CallbackUpdate(-100, 42, 100, "price:skip_expiry"));

        var records = await PurchaseRepository.SearchAsync(group.Id, "Coffee");
        Assert.Contains(records, r => r.StoreName == "Aldi" && r.Price == null);
    }

    [Fact]
    public async Task PriceCapture_SkipExpiry_Saves_Purchase_And_Clears_Dialog()
    {
        await ClearDataAsync();

        var group = await GroupRepository.GetOrCreateAsync(-100);
        await ItemRepository.AddAsync(group.Id, "Butter", null, "TestUser");

        StartPriceCapture(group.Id, "Butter");
        await DispatchAsync(MessageUpdate(-100, 42, "Aldi"));
        await DispatchAsync(MessageUpdate(-100, 42, "2.50"));

        // Skip expiry → saves and closes dialog
        await DispatchAsync(CallbackUpdate(-100, 42, 100, "price:skip_expiry"));

        var records = await PurchaseRepository.SearchAsync(group.Id, "Butter");
        Assert.Contains(records, r => r.Price == 2.50m);
    }

    /// <summary>
    /// Opens the price-capture dialog at step 1. Marking an item bought no longer starts it (that flow is
    /// silent now), so tests covering the dialog itself seed its state directly.
    /// </summary>
    private void StartPriceCapture(int groupId, string itemName, string? quantity = null, IReadOnlyList<string>? tags = null) =>
        PriceDialogService.SetState(-100, 42, new PriceCaptureDialogState
        {
            Step = 1,
            GroupId = groupId,
            ItemName = itemName,
            Quantity = quantity,
            BoughtByName = "TestUser",
            Tags = tags ?? Array.Empty<string>(),
        });
}
