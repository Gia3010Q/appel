using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Eldorado.Core;
using Forms = System.Windows.Forms;

namespace Eldorado.Desktop;

public partial class MainWindow : Window
{
    private readonly bool previewMode;
    private sealed record DeliveryChoice(string Value, string Label);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly DispatcherTimer pollTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly LocalStore store;
    private readonly PriceCatalogService priceCatalog;
    private readonly DispatcherTimer priceTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private bool refreshingPrices;
    private readonly Forms.NotifyIcon? tray;
    private SellerApi? api;
    private ShopPoller? poller;
    private Credentials? credentials;
    private List<Order> orders = new();
    private List<Offer> offers = new();
    private List<BoostRequest> requests = new();
    private bool quitting;
    private bool ready;
    private bool working;
    private bool suppressRequestSelection;
    private BoostRequest? quoteRequest;
    private (string Stream, string Id)? notificationTarget;

    public MainWindow(bool previewMode = false)
    {
        this.previewMode = previewMode;
        InitializeComponent();
        string dataFolder = previewMode
            ? Path.Combine(Path.GetTempPath(), "EldoradoPreview", Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EldoradoShop");
        Directory.CreateDirectory(dataFolder);
        store = new LocalStore(Path.Combine(dataFolder, "shop.db"));
        priceCatalog = new PriceCatalogService(http, Path.Combine(dataFolder, "prices-cache.json"));
        priceTimer.Tick += async (_, _) => await RefreshPriceCatalogAsync();
        if (!previewMode)
        {
        tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico")),
            Text = "Eldorado · Quản lý shop",
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Mở ứng dụng", null, (_, _) => Dispatcher.Invoke(RestoreWindow));
        menu.Items.Add("Làm mới", null, async (_, _) => await Dispatcher.InvokeAsync(() => RunAsync(SyncAllAsync)));
        menu.Items.Add("Thoát", null, (_, _) => Dispatcher.Invoke(RequestQuit));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreWindow);
        tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(OpenNotification);
        }
        QuoteCurrency.ItemsSource = SellerApi.Currencies;
        QuoteDelivery.ItemsSource = SellerApi.DeliveryTimes.Select(value => new DeliveryChoice(value, DeliveryLabel(value))).ToList();
        QuoteDelivery.DisplayMemberPath = nameof(DeliveryChoice.Label);
        QuoteDelivery.SelectedValuePath = nameof(DeliveryChoice.Value);
        QuoteCurrency.SelectedItem = "USD";
        QuoteDelivery.SelectedIndex = 0;
        pollTimer.Tick += async (_, _) => await PollStreamsAsync();
        ready = true;
        OrdersNav.IsChecked = true;
    }

    public void LoadPreviewData()
    {
        if (!previewMode) throw new InvalidOperationException("Sample data requires preview mode.");
        var now = new DateTimeOffset(2026, 11, 6, 14, 32, 8, TimeSpan.FromHours(7));
        requests = new List<BoostRequest>
        {
            new("a340c101-4931-41bf-9f02-000000000001", "Valorant", "Rank Boost", "MinhAnh_92", now.AddMinutes(-1)),
            new("a340c101-4931-41bf-9f02-000000000002", "Roblox", "Nhiệm vụ", "HuyGaming", now.AddMinutes(-3)),
            new("a340c101-4931-41bf-9f02-000000000003", "Genshin Impact", "Khám phá bản đồ", "Luna_07", now.AddMinutes(-6)),
            new("a340c101-4931-41bf-9f02-000000000004", "League of Legends", "Rank Boost", "AlexNguyen", now.AddMinutes(-9)),
            new("a340c101-4931-41bf-9f02-000000000005", "Roblox", "Luyện cấp", "BearCloud", now.AddMinutes(-12)),
            new("a340c101-4931-41bf-9f02-000000000006", "Valorant", "Net Wins", "KayTran", now.AddMinutes(-18))
        };
        requests[0] = requests[0] with { Description = "Dữ liệu minh họa: Gold 2 → Platinum 1, máy chủ Asia, hoàn thành trong 2 ngày." };
        RequestsGrid.ItemsSource = requests;
        RequestsGrid.SelectedIndex = 0;
        QuotePrice.Text = "18.50";
        QuoteCurrency.SelectedItem = "USD";
        QuoteDelivery.SelectedValue = "Day2";
        RequestsCountText.Text = "6";
        OrdersCountText.Text = "3";
        LastBoostSyncText.Text = "14:32:08";
        BoostNav.IsChecked = true;
        PageHint.Text = "Dữ liệu minh họa · giao diện mẫu để xem trước khi kết nối shop.";
        RequestContent.Text = "Dữ liệu minh họa\nRank hiện tại: Gold 2 → Mục tiêu: Platinum 1\nMáy chủ: Asia · Chế độ: Solo\nYêu cầu thêm: hoàn thành trong 2 ngày, báo trước khi bắt đầu.";
        StatusText.Text = "Dữ liệu minh họa · 6 yêu cầu cày thuê · tự kiểm tra mỗi 15 giây khi kết nối shop thật";
    }

    private static string DeliveryLabel(string value)
    {
        if (value.StartsWith("Minute", StringComparison.Ordinal)) return value[6..] + " phút";
        if (value.StartsWith("Hour", StringComparison.Ordinal)) return value[4..] + " giờ";
        if (value.StartsWith("Day", StringComparison.Ordinal)) return value[3..] + " ngày";
        return value;
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (previewMode)
        {
            return;
        }
        UpdatePriceCatalogView();
        priceTimer.Start();
        _ = RefreshPriceCatalogAsync();
        await RunAsync(async () =>
        {
            credentials = CredentialStore.Load();
            if (credentials is null)
            {
                SettingsNav.IsChecked = true;
                return;
            }
            ClientIdInput.Text = credentials.ClientId;
            CredentialHint.Text = "Đã lưu secret. Để trống ô secret để tiếp tục dùng thông tin hiện tại.";
            CreateApi();
            pollTimer.Start();
            await SyncAllAsync();
        });
        if (api is not null) pollTimer.Start();
    }

    private void CreateApi()
    {
        api = new SellerApi(http, () => Task.FromResult(credentials ?? throw new InvalidOperationException("Cần kết nối shop trước.")));
        poller = new ShopPoller(api);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (quitting || !await operation.WaitAsync(0)) return;
        working = true;
        WorkArea.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (ApiException ex)
        {
            StatusText.Text = ex.OutcomeUnknown
                ? "Chưa xác định kết quả thao tác. Kiểm tra trên Eldorado trước khi thực hiện lại."
                : "Eldorado chưa xử lý được yêu cầu. " + ex.Message;
        }
        catch (HttpRequestException)
        {
            StatusText.Text = "Không kết nối được Eldorado. Đồng bộ tự động vẫn tiếp tục kiểm tra riêng từng mục.";
        }
        catch (TaskCanceledException)
        {
            StatusText.Text = "Kết nối quá thời gian chờ. Hãy làm mới để kiểm tra dữ liệu.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Runtime.InteropServices.ExternalException)
        {
            StatusText.Text = "Không hoàn tất được thao tác. " + ex.Message;
        }
        catch (Exception)
        {
            StatusText.Text = "Không hoàn tất được thao tác. Kiểm tra kết nối và dữ liệu đã lưu trước khi thử lại.";
        }
        finally
        {
            working = false;
            WorkArea.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            operation.Release();
        }
    }

    private async Task SyncAllAsync()
    {
        if (api is null)
        {
            StatusText.Text = "Chưa kết nối shop. Vào Kết nối shop để nhập credentials.";
            return;
        }
        await PollStreamsAsync(force: true);
        if (OffersPage.Visibility == Visibility.Visible) await LoadOffersAsync();
    }

    private async Task PollStreamsAsync(bool force = false)
    {
        ShopPoller? current = poller;
        if (current is null || quitting) return;
        var newEvents = new List<(string Stream, string Id)>();
        try
        {
            // Each callback updates immediately. A slow or failed order read cannot hold up requests.
            await Task.WhenAll(PollOrdersAsync(current, force, newEvents), PollRequestsAsync(current, force, newEvents));
            if (ReferenceEquals(current, poller)) ShowNewNotification(newEvents);
        }
        catch (Exception)
        {
            StatusText.Text = "Không lưu được mốc đồng bộ. Kiểm tra dung lượng lưu trữ trên máy.";
        }
    }

    private async Task PollOrdersAsync(ShopPoller current, bool force, List<(string Stream, string Id)> events)
    {
        StreamSnapshot<Order> snapshot = await current.PollOrdersAsync(force);
        if (!ReferenceEquals(current, poller) || quitting) return;
        if (snapshot.IsFresh)
        {
            orders = snapshot.Items.ToList();
            ApplyOrderFilter();
            OrdersCountText.Text = orders.Count(x => x.State == "Paid").ToString();
            ObserveNew("orders", orders.Select(x => x.Id), events);
        }
        if (snapshot.Error is not null)
            StatusText.Text = $"Đơn hàng chưa cập nhật · lần thành công {FormatSync(snapshot.LastSuccess)} · thử lại lúc {snapshot.NextAttempt.ToLocalTime():HH:mm:ss}. {snapshot.Error}";
    }

    private async Task PollRequestsAsync(ShopPoller current, bool force, List<(string Stream, string Id)> events)
    {
        StreamSnapshot<BoostRequest> snapshot = await current.PollRequestsAsync(force);
        if (!ReferenceEquals(current, poller) || quitting) return;
        if (snapshot.IsFresh)
        {
            requests = snapshot.Items.Select(item => item with
            {
                Description = requests.FirstOrDefault(previous => previous.Id == item.Id)?.Description ?? item.Description
            }).ToList();
            string? selectedId = quoteRequest?.Id;
            suppressRequestSelection = true;
            try
            {
                RequestsGrid.ItemsSource = requests;
                RequestsGrid.SelectedItem = requests.FirstOrDefault(x => x.Id == selectedId);
            }
            finally { suppressRequestSelection = false; }
            // Keep the in-progress quote untouched, including when its row leaves the latest page.
            RequestsCountText.Text = requests.Count.ToString();
            ObserveNew("requests", requests.Select(x => x.Id), events);
            LastBoostSyncText.Text = FormatSync(snapshot.LastSuccess);
            StatusText.Text = $"Yêu cầu cày thuê đã cập nhật lúc {FormatSync(snapshot.LastSuccess)} · tự kiểm tra mỗi 15 giây · {requests.Count} yêu cầu mới nhất";
        }
        if (snapshot.Error is not null)
        {
            LastBoostSyncText.Text = "Đang chờ thử lại";
            StatusText.Text = $"Yêu cầu cày thuê chưa cập nhật · lần thành công {FormatSync(snapshot.LastSuccess)} · thử lại lúc {snapshot.NextAttempt.ToLocalTime():HH:mm:ss}. {snapshot.Error}";
        }
    }

    private static string FormatSync(DateTimeOffset? time) => time?.ToLocalTime().ToString("HH:mm:ss") ?? "chưa có";

    private async Task LoadOffersAsync()
    {
        SellerApi? current = api;
        if (current is null) return;
        StatusText.Text = "Đang tải offer. Yêu cầu cày thuê vẫn tự cập nhật riêng.";
        List<Offer> result = await current.GetOffersAsync();
        if (!ReferenceEquals(current, api)) return;
        offers = result;
        ApplyOfferFilter();
        StatusText.Text = $"Đã tải {offers.Count} offer lúc {DateTime.Now:HH:mm:ss}. " + string.Join(" ", current.OfferWarnings);
    }

    private void ObserveNew(string stream, IEnumerable<string> ids, List<(string Stream, string Id)> newEvents)
    {
        string scopedStream = $"{credentials!.ClientId}:{stream}";
        List<string> newIds = store.Observe(scopedStream, ids);
        newEvents.AddRange(newIds.Select(id => (stream, id)));
    }

    private void ShowNewNotification(List<(string Stream, string Id)> newEvents)
    {
        if (newEvents.Count == 0) return;
        // One balloon per sync preserves the click target when both streams have new rows.
        notificationTarget = newEvents[0];
        int orderCount = newEvents.Count(x => x.Stream == "orders");
        int requestCount = newEvents.Count - orderCount;
        string summary = $"{orderCount} đơn hàng và {requestCount} yêu cầu cày thuê mới.";
        tray?.ShowBalloonTip(8000, "Eldorado · Có cập nhật mới", summary + " Bấm để xem mục mới nhất.", Forms.ToolTipIcon.Info);
    }

    private void OpenNotification()
    {
        RestoreWindow();
        if (notificationTarget is not { } target) return;
        if (target.Stream == "orders")
        {
            OrderSearch.Clear();
            PendingOnly.IsChecked = false;
            BoostOrdersOnly.IsChecked = false;
            OrdersNav.IsChecked = true;
            OrdersGrid.SelectedItem = orders.FirstOrDefault(x => x.Id == target.Id);
            if (OrdersGrid.SelectedItem is not null) OrdersGrid.ScrollIntoView(OrdersGrid.SelectedItem);
        }
        else
        {
            BoostNav.IsChecked = true;
            RequestsGrid.SelectedItem = requests.FirstOrDefault(x => x.Id == target.Id);
            if (RequestsGrid.SelectedItem is not null) RequestsGrid.ScrollIntoView(RequestsGrid.SelectedItem);
        }
    }

    private async void Navigate(object sender, RoutedEventArgs e)
    {
        if (!ready || sender is not RadioButton item) return;
        int page = int.Parse((string)item.Tag, CultureInfo.InvariantCulture);
        OrdersPage.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        OffersPage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        BoostPage.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = new[] { "Đơn hàng", "Offer", "Cày thuê", "Kết nối shop" }[page];
        PageHint.Text = new[]
        {
            "Theo dõi tối đa 50 đơn mới nhất và xác nhận giao hàng.",
            "Tìm offer theo tên hoặc game, kiểm tra giá trước khi lưu.",
            "Chọn yêu cầu khách, đặt giá và thời gian hoàn thành.",
            "Thông tin kết nối được lưu an toàn trên máy Windows này."
        }[page];
        if (page == 1 && !previewMode) await RunAsync(LoadOffersAsync);
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RunAsync(SyncAllAsync);

    private void OrderFilterChanged(object sender, RoutedEventArgs e)
    {
        if (ready) ApplyOrderFilter();
    }

    private void ApplyOrderFilter()
    {
        string? selectedId = (OrdersGrid.SelectedItem as Order)?.Id;
        string search = OrderSearch.Text.Trim();
        var filtered = orders.Where(x => (!PendingOnly.IsChecked.GetValueOrDefault() || x.State == "Paid")
            && (!BoostOrdersOnly.IsChecked.GetValueOrDefault() || x.Category.Contains("Boost", StringComparison.OrdinalIgnoreCase))
            && Matches(search, x.Id, x.Title, x.Game, x.Buyer)).ToList();
        OrdersGrid.ItemsSource = filtered;
        OrdersGrid.SelectedItem = filtered.FirstOrDefault(x => x.Id == selectedId);
    }

    private void OfferFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (ready) ApplyOfferFilter();
    }

    private void ApplyOfferFilter()
    {
        string? selectedId = (OffersGrid.SelectedItem as Offer)?.Id;
        var filtered = offers.Where(x => Matches(OfferSearch.Text.Trim(), x.Id, x.Title, x.Game)).ToList();
        OffersGrid.ItemsSource = filtered;
        OffersGrid.SelectedItem = filtered.FirstOrDefault(x => x.Id == selectedId);
    }

    private static bool Matches(string query, params string[] fields) => fields.Any(x => x.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    private void OrderSelected(object sender, SelectionChangedEventArgs e)
    {
        var order = OrdersGrid.SelectedItem as Order;
        DeliverButton.IsEnabled = order is { State: "Paid" };
        OrderDetails.Text = order is null ? "Chọn một đơn để xem thông tin."
            : $"{order.Title}\n\nMã đơn\n{order.Id}\n\nGame\n{order.Game}\n\nNgười mua\n{order.Buyer}\n\nTrạng thái\n{order.State}\n\nGiá\n{order.Price:G29} {order.Currency}\n\nHạn giao\n{(order.Deadline is { } deadline ? deadline.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "Eldorado chưa cung cấp")}";
    }

    private async void DeliverClick(object sender, RoutedEventArgs e)
    {
        if (OrdersGrid.SelectedItem is not Order order || api is null) return;
        if (!Confirm($"Xác nhận đã hoàn tất giao hàng?\n\n{order.Title}\nMã đơn {order.Id}", "Xác nhận giao hàng")) return;
        await RunAsync(async () =>
        {
            await api.DeliverAsync(order);
            StatusText.Text = "Eldorado đã xác nhận giao hàng. Đang làm mới danh sách…";
            await SyncAllAsync();
        });
    }

    private void OfferSelected(object sender, SelectionChangedEventArgs e)
    {
        var offer = OffersGrid.SelectedItem as Offer;
        SavePriceButton.IsEnabled = offer is not null;
        SaveQuantityButton.IsEnabled = offer?.Quantity is not null;
        OfferQuantity.IsEnabled = offer?.Quantity is not null;
        OfferDetails.Text = offer is null ? "Chọn một offer để chỉnh sửa." : $"{offer.Title}\n\nGame {offer.Game}\n{offer.Kind}\n\nGiá hiện tại {offer.Price:G29} {offer.Currency}\nSố lượng {(offer.Quantity?.ToString() ?? "Theo loại offer")}";
        OfferPrice.Text = offer?.Price.ToString(CultureInfo.CurrentCulture) ?? "";
        OfferQuantity.Text = offer?.Quantity?.ToString(CultureInfo.CurrentCulture) ?? "";
    }

    private async void SavePriceClick(object sender, RoutedEventArgs e)
    {
        if (OffersGrid.SelectedItem is not Offer offer || api is null || !TryPrice(OfferPrice.Text, out decimal price)) return;
        if (!Confirm($"{offer.Title}\nMã offer {offer.Id}\n\nGiá cũ {offer.Price:G29} {offer.Currency}\nGiá mới {price:G29} {offer.Currency}\n\nLưu thay đổi lên Eldorado?", "Kiểm tra giá")) return;
        await RunAsync(async () => { await api.ChangePriceAsync(offer, price); await SyncAllAsync(); });
    }

    private async void SaveQuantityClick(object sender, RoutedEventArgs e)
    {
        if (OffersGrid.SelectedItem is not Offer offer || api is null || offer.Quantity is null) return;
        if (!int.TryParse(OfferQuantity.Text, out int quantity) || quantity < 0)
        {
            ShowInfo("Nhập số lượng nguyên từ 0 trở lên.");
            return;
        }
        if (!Confirm($"{offer.Title}\nMã offer {offer.Id}\n\nSố lượng cũ {offer.Quantity}\nSố lượng mới {quantity}\n\nLưu thay đổi lên Eldorado?", "Kiểm tra số lượng")) return;
        await RunAsync(async () => { await api.ChangeQuantityAsync(offer, quantity); await SyncAllAsync(); });
    }

    private async void RequestSelected(object sender, SelectionChangedEventArgs e)
    {
        if (suppressRequestSelection) return;
        var request = RequestsGrid.SelectedItem as BoostRequest;
        quoteRequest = request;
        SaveDraftButton.IsEnabled = request is not null;
        CopyQuoteButton.IsEnabled = request is not null;
        RequestDetails.Text = request is null ? "Chọn yêu cầu của khách." : $"{request.Category} · {request.Game}\nKhách {request.Buyer}\n{request.Id}";
        QuoteDraft? draft = request is null ? null : store.LoadDraft(request.Id);
        QuotePrice.Text = draft?.Price.ToString(CultureInfo.CurrentCulture) ?? "";
        QuoteCurrency.SelectedItem = draft?.Currency ?? "USD";
        QuoteDelivery.SelectedValue = draft?.DeliveryTime ?? SellerApi.DeliveryTimes.FirstOrDefault();
        SetQuoteState(request is null ? null : store.GetStatus(request.Id));
        await LoadRequestDetailsAsync(request);
    }

    private async Task LoadRequestDetailsAsync(BoostRequest? request)
    {
        if (request is null)
        {
            RequestContent.Text = "Chọn một yêu cầu để đọc nội dung chi tiết.";
            return;
        }
        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            RequestContent.Text = request.Description;
            return;
        }
        SellerApi? current = api;
        if (current is null) return;
        RequestContent.Text = "Đang tải nội dung yêu cầu…";
        string text;
        string? description = null;
        try
        {
            description = await current.GetRequestDetailsAsync(request.Id);
            text = description ?? "Eldorado chưa trả về nội dung chi tiết cho kết nối này. Bấm Mở yêu cầu trên Eldorado để xem đúng yêu cầu của khách.";
        }
        catch (ApiException error)
        {
            text = error.Message + "\nMở yêu cầu trên Eldorado để đọc đầy đủ nội dung trước khi báo giá.";
        }
        catch (Exception)
        {
            text = "Chưa tải được nội dung chi tiết. Bấm Mở yêu cầu trên Eldorado để đọc yêu cầu trước khi báo giá.";
        }
        if (ReferenceEquals(current, api) && quoteRequest?.Id == request.Id)
        {
            RequestContent.Text = text;
            if (!string.IsNullOrWhiteSpace(description))
            {
                quoteRequest = request with { Description = description };
                requests = requests.Select(item => item.Id == request.Id ? quoteRequest : item).ToList();
                suppressRequestSelection = true;
                try
                {
                    RequestsGrid.ItemsSource = requests;
                    RequestsGrid.SelectedItem = requests.FirstOrDefault(item => item.Id == request.Id);
                }
                finally { suppressRequestSelection = false; }
            }
        }
    }

    private void SetQuoteState(string? status)
    {
        bool locked = status is "Sending" or "Unknown" or "Sent" or "UserConfirmed";
        ApplyCatalogPriceButton.IsEnabled = quoteRequest is not null && !locked && priceCatalog.Current is not null;
        SendQuoteButton.IsEnabled = quoteRequest is not null && !locked;
        SaveDraftButton.IsEnabled = quoteRequest is not null && !locked;
        QuotePrice.IsEnabled = !locked;
        QuoteCurrency.IsEnabled = !locked;
        QuoteDelivery.IsEnabled = !locked;
        ResolveQuoteButton.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        QuoteStatus.Text = status switch
        {
            "Sent" => "Đã gửi · Eldorado đã xác nhận",
            "UserConfirmed" => "Đã đối chiếu trên website · bạn đã xác nhận",
            "Unknown" or "Sending" => "Cần đối chiếu · chưa xác định kết quả gửi",
            _ => "Bản nháp · chưa gửi lên Eldorado"
        };
    }

    private QuoteDraft? ReadDraft()
    {
        if (quoteRequest is not BoostRequest request || !TryPrice(QuotePrice.Text, out decimal price)) return null;
        if (QuoteCurrency.SelectedItem is not string currency || QuoteDelivery.SelectedValue is not string delivery)
        {
            ShowInfo("Chọn đơn vị tiền và thời gian hoàn thành.");
            return null;
        }
        return new QuoteDraft(request.Id, price, currency, delivery, store.GetStatus(request.Id) ?? "Draft");
    }

    private void SaveDraftClick(object sender, RoutedEventArgs e)
    {
        QuoteDraft? draft = ReadDraft();
        if (draft is null) return;
        store.SaveDraft(draft);
        SetQuoteState(draft.Status);
        StatusText.Text = "Đã lưu bản nháp trên máy.";
    }

    private void CopyQuoteClick(object sender, RoutedEventArgs e)
    {
        QuoteDraft? draft = ReadDraft();
        if (draft is null || quoteRequest is not BoostRequest request) return;
        System.Windows.Clipboard.SetText($"Yêu cầu {draft.RequestId}\n{request.Category} · Game {request.Game}\nNgười mua {request.Buyer}\nGiá {draft.Price.ToString(CultureInfo.InvariantCulture)} {draft.Currency}\nThời gian {DeliveryLabel(draft.DeliveryTime)}");
        StatusText.Text = "Đã sao chép báo giá. Chỉ gửi sau khi bạn kiểm tra đầy đủ yêu cầu.";
    }

    private async void SendQuoteClick(object sender, RoutedEventArgs e)
    {
        QuoteDraft? draft = ReadDraft();
        if (draft is null || quoteRequest is not BoostRequest request || api is null) return;
        if (store.GetStatus(request.Id) is "Sending" or "Unknown" or "Sent" or "UserConfirmed") return;
        if (!Confirm($"Gửi báo giá này cho khách?\n\n{request.Category} · Game {request.Game}\nKhách {request.Buyer}\nMã yêu cầu {request.Id}\n\nGiá {draft.Price:G29} {draft.Currency}\nThời gian {DeliveryLabel(draft.DeliveryTime)}\n\nHãy đối chiếu đầy đủ nội dung yêu cầu trên Eldorado trước khi gửi.", "Kiểm tra báo giá")) return;
        await RunAsync(async () =>
        {
            store.SaveDraft(draft);
            store.SetStatus(request.Id, "Sending");
            SetQuoteState("Sending");
            try
            {
                await api.SendQuoteAsync(request, draft.Price, draft.Currency, draft.DeliveryTime);
                store.SetStatus(request.Id, "Sent");
                SetQuoteState("Sent");
                StatusText.Text = "Đã gửi báo giá. Eldorado đã xác nhận.";
            }
            catch (ApiException ex) when (!ex.OutcomeUnknown)
            {
                store.SetStatus(request.Id, "Draft");
                SetQuoteState("Draft");
                throw;
            }
            catch
            {
                store.SetStatus(request.Id, "Unknown");
                SetQuoteState("Unknown");
                throw;
            }
        });
    }

    private async void ResolveQuoteClick(object sender, RoutedEventArgs e)
    {
        if (quoteRequest is not BoostRequest request) return;
        var result = System.Windows.MessageBox.Show(this,
            $"Hãy mở Eldorado và đối chiếu yêu cầu {request.Id}.\n\nChọn Có nếu báo giá đã xuất hiện.\nChọn Không nếu bạn đã xác nhận chưa có báo giá và muốn mở lại nút gửi.\nChọn Hủy để tiếp tục giữ trạng thái hiện tại.",
            "Đối chiếu kết quả", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return;
        await RunAsync(async () =>
        {
            string status = result == MessageBoxResult.Yes ? "UserConfirmed" : "Draft";
            if (status == "Draft" && api is not null)
                await api.AcknowledgeUnsentQuoteAsync(request.Id);
            store.SetStatus(request.Id, status);
            SetQuoteState(status);
            StatusText.Text = "Đã ghi nhận kết quả do bạn đối chiếu trên Eldorado.";
        });
    }

    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        string id = ClientIdInput.Text.Trim();
        string secret = ClientSecretInput.Password;
        if (secret.Length == 0 && credentials?.ClientId == id) secret = credentials.ClientSecret;
        if (id.Length == 0 || secret.Length == 0)
        {
            ShowInfo("Nhập đầy đủ Client ID và Client Secret.");
            return;
        }
        var nextCredentials = new Credentials(id, secret);
        await RunAsync(async () =>
        {
            CredentialStore.Save(nextCredentials);
            credentials = nextCredentials;
            ClientSecretInput.Clear();
            CredentialHint.Text = "Đã lưu secret trong Windows Credential Manager.";
            orders.Clear();
            offers.Clear();
            requests.Clear();
            ApplyOrderFilter();
            ApplyOfferFilter();
            RequestsGrid.ItemsSource = requests;
            CreateApi();
            pollTimer.Start();
            await SyncAllAsync();
        });
    }

    private void DisconnectClick(object sender, RoutedEventArgs e)
    {
        if (!Confirm("Ngắt kết nối và xóa thông tin đăng nhập đã lưu trên máy?", "Ngắt kết nối")) return;
        pollTimer.Stop();
        CredentialStore.Delete();
        credentials = null;
        api = null;
        poller = null;
        quoteRequest = null;
        ClientIdInput.Clear();
        ClientSecretInput.Clear();
        CredentialHint.Text = "Đã xóa thông tin đăng nhập.";
        orders.Clear();
        offers.Clear();
        requests.Clear();
        ApplyOrderFilter();
        ApplyOfferFilter();
        RequestsGrid.ItemsSource = requests;
        StatusText.Text = "Đã ngắt kết nối shop.";
    }

    private bool TryPrice(string value, out decimal price)
    {
        bool parsed = decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out price);
        if (!parsed) parsed = decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out price);
        if (parsed && price > 0) return true;
        ShowInfo("Nhập giá lớn hơn 0. Ví dụ 12.50, dùng dấu thập phân theo thiết lập Windows.");
        return false;
    }

    private bool Confirm(string text, string title) => System.Windows.MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void ShowInfo(string text) => System.Windows.MessageBox.Show(this, text, "Eldorado", MessageBoxButton.OK, MessageBoxImage.Information);
    private static void OpenWebsite(string path) => Process.Start(new ProcessStartInfo("https://www.eldorado.gg" + path) { UseShellExecute = true });
    private void OpenChatClick(object sender, RoutedEventArgs e) => OpenWebsite("/dashboard/messages");
    private void OpenRequestClick(object sender, RoutedEventArgs e)
    {
        if (quoteRequest is not null) OpenWebsite("/boosting-request/" + Uri.EscapeDataString(quoteRequest.Id));
        else OpenWebsite("/dashboard");
    }
    private void OpenDashboardClick(object sender, RoutedEventArgs e) => OpenWebsite("/dashboard");
    private void OpenSellerApiClick(object sender, RoutedEventArgs e) => OpenWebsite("/dashboard/seller-api");

    private void ShowBoostOrdersClick(object sender, RoutedEventArgs e)
    {
        OrdersNav.IsChecked = true;
        OrderSearch.Clear();
        BoostOrdersOnly.IsChecked = true;
        PendingOnly.IsChecked = false;
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void WindowStateChanged(object? sender, EventArgs e)
    {
        // Keep the taskbar button when minimized so the app remains easy to find.
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!quitting && CloseToTray.IsChecked == true)
        {
            e.Cancel = true;
            Hide();
            tray?.ShowBalloonTip(6000, "Eldorado vẫn đang chạy", "Mở lại bằng biểu tượng Eldorado ở khay hệ thống. Bấm mũi tên cạnh đồng hồ nếu biểu tượng đang ẩn.", Forms.ToolTipIcon.Info);
        }
        else if (!quitting)
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
        }
    }

    private void QuitClick(object sender, RoutedEventArgs e) => RequestQuit();

    private async void RefreshPricesClick(object sender, RoutedEventArgs e) => await RefreshPriceCatalogAsync();

    private async Task RefreshPriceCatalogAsync()
    {
        if (previewMode || quitting || refreshingPrices) return;
        refreshingPrices = true;
        RefreshPricesButton.IsEnabled = false;
        try
        {
            await priceCatalog.RefreshAsync();
            if (quitting) return;
            UpdatePriceCatalogView();
            CatalogStatus.Text = $"{priceCatalog.Current!.Items.Count} dịch vụ · kiểm tra lúc {priceCatalog.LastChecked:HH:mm:ss} · tự cập nhật mỗi 5 phút";
        }
        catch (Exception)
        {
            if (quitting) return;
            CatalogStatus.Text = priceCatalog.Current is null
                ? "Chưa tải được bảng giá. Kiểm tra mạng hoặc file prices.json rồi cập nhật lại."
                : "Chưa tải được bản mới. Đang dùng bảng giá hợp lệ đã lưu trên máy.";
        }
        finally
        {
            refreshingPrices = false;
            if (!quitting) RefreshPricesButton.IsEnabled = true;
        }
    }

    private void UpdatePriceCatalogView()
    {
        string? selected = (CatalogItems.SelectedItem as CatalogItem)?.Name;
        CatalogItems.ItemsSource = priceCatalog.Current?.Items;
        CatalogItems.SelectedItem = priceCatalog.Current?.Items.FirstOrDefault(item => item.Name == selected);
        SetQuoteState(quoteRequest is null ? null : store.GetStatus(quoteRequest.Id));
    }

    private void ApplyCatalogPriceClick(object sender, RoutedEventArgs e)
    {
        if (quoteRequest is null || CatalogItems.SelectedItem is not CatalogItem item || priceCatalog.Current is null) return;
        if (store.GetStatus(quoteRequest.Id) is "Sending" or "Unknown" or "Sent" or "UserConfirmed") return;
        QuotePrice.Text = item.Price.ToString(CultureInfo.CurrentCulture);
        QuoteCurrency.SelectedItem = priceCatalog.Current.Currency;
        QuoteDelivery.SelectedValue = item.Delivery;
        StatusText.Text = $"Đã lấy giá {item.Name}: {item.Price:G29} {priceCatalog.Current.Currency}. Kiểm tra yêu cầu của khách trước khi gửi.";
    }

    private void RequestQuit()
    {
        if (working)
        {
            RestoreWindow();
            ShowInfo("Một thao tác đang chạy. Chờ thao tác hoàn tất rồi thoát để kiểm tra được kết quả.");
            return;
        }
        quitting = true;
        pollTimer.Stop();
        priceTimer.Stop();
        if (tray is not null)
        {
            tray.Visible = false;
            tray.Dispose();
        }
        http.Dispose();
        store.Dispose();
        Close();
    }
}
