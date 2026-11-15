using Dib4t.Contracts.Account;
using Dib4t.Contracts.Settings;

namespace Dib4t.Client.Api;

/// <summary>User management of <c>/api/account</c> (settings, <c>canSeeDefinicoes</c>; PLAN.md §6, §8.2).</summary>
public interface IUsersApi
{
    Task<ApiResult<IReadOnlyList<UserListItemResponse>>> ListAsync();

    Task<ApiResult<UserResponse>> CreateAsync(UserRequest user);

    Task<ApiResult<UserResponse>> UpdateAsync(string id, UserRequest user);
}

/// <summary><c>/api/prices</c>.</summary>
public interface IPricesApi
{
    Task<ApiResult<PricesResponse>> GetAsync();

    Task<ApiResult<PricesResponse>> UpdateAsync(PricesRequest prices);
}

/// <summary>The payment methods of <c>/api/entity</c> (the rest of <c>IEntityApi</c> comes with the entities slice).</summary>
public interface IPaymentMethodsApi
{
    Task<ApiResult<IReadOnlyList<PaymentMethodResponse>>> ListAsync();

    Task<ApiResult<PaymentMethodResponse>> AddAsync(string name);

    Task<ApiResult<PaymentMethodResponse>> DeleteAsync(string id);
}

/// <summary><c>/api/printer-settings</c>.</summary>
public interface IPrinterSettingsApi
{
    Task<ApiResult<IReadOnlyList<PrinterSettingsResponse>>> ListAsync();

    Task<ApiResult<PrinterSettingsResponse>> CreateAsync(PrinterSettingsRequest printer);

    Task<ApiResult<PrinterSettingsResponse>> UpdateAsync(string id, PrinterSettingsRequest printer);

    Task<ApiResult<PrinterSettingsResponse>> DeleteAsync(string id);
}

/// <inheritdoc cref="IUsersApi"/>
public sealed class UsersApi(HttpClient http) : IUsersApi
{
    public Task<ApiResult<IReadOnlyList<UserListItemResponse>>> ListAsync() =>
        ApiCalls.SendAsync<IReadOnlyList<UserListItemResponse>>(http, HttpMethod.Get, "api/account/users");

    public Task<ApiResult<UserResponse>> CreateAsync(UserRequest user) =>
        ApiCalls.SendAsync<UserResponse>(http, HttpMethod.Post, "api/account/create", user);

    public Task<ApiResult<UserResponse>> UpdateAsync(string id, UserRequest user) =>
        ApiCalls.SendAsync<UserResponse>(http, HttpMethod.Put, "api/account/" + Uri.EscapeDataString(id), user);
}

/// <inheritdoc cref="IPricesApi"/>
public sealed class PricesApi(HttpClient http) : IPricesApi
{
    public Task<ApiResult<PricesResponse>> GetAsync() => ApiCalls.SendAsync<PricesResponse>(http, HttpMethod.Get, "api/prices");

    public Task<ApiResult<PricesResponse>> UpdateAsync(PricesRequest prices) =>
        ApiCalls.SendAsync<PricesResponse>(http, HttpMethod.Post, "api/prices", prices);
}

/// <inheritdoc cref="IPaymentMethodsApi"/>
public sealed class PaymentMethodsApi(HttpClient http) : IPaymentMethodsApi
{
    public Task<ApiResult<IReadOnlyList<PaymentMethodResponse>>> ListAsync() =>
        ApiCalls.SendAsync<IReadOnlyList<PaymentMethodResponse>>(http, HttpMethod.Get, "api/entity/getAllPaymentConditions");

    public Task<ApiResult<PaymentMethodResponse>> AddAsync(string name) =>
        ApiCalls.SendAsync<PaymentMethodResponse>(http, HttpMethod.Post, "api/entity/addPaymentMethod", new PaymentMethodRequest(name));

    public Task<ApiResult<PaymentMethodResponse>> DeleteAsync(string id) =>
        ApiCalls.SendAsync<PaymentMethodResponse>(http, HttpMethod.Delete, "api/entity/deletePaymentMethod/" + Uri.EscapeDataString(id));
}

/// <inheritdoc cref="IPrinterSettingsApi"/>
public sealed class PrinterSettingsApi(HttpClient http) : IPrinterSettingsApi
{
    public Task<ApiResult<IReadOnlyList<PrinterSettingsResponse>>> ListAsync() =>
        ApiCalls.SendAsync<IReadOnlyList<PrinterSettingsResponse>>(http, HttpMethod.Get, "api/printer-settings");

    public Task<ApiResult<PrinterSettingsResponse>> CreateAsync(PrinterSettingsRequest printer) =>
        ApiCalls.SendAsync<PrinterSettingsResponse>(http, HttpMethod.Post, "api/printer-settings", printer);

    public Task<ApiResult<PrinterSettingsResponse>> UpdateAsync(string id, PrinterSettingsRequest printer) =>
        ApiCalls.SendAsync<PrinterSettingsResponse>(http, HttpMethod.Put, "api/printer-settings/" + Uri.EscapeDataString(id), printer);

    public Task<ApiResult<PrinterSettingsResponse>> DeleteAsync(string id) =>
        ApiCalls.SendAsync<PrinterSettingsResponse>(http, HttpMethod.Delete, "api/printer-settings/" + Uri.EscapeDataString(id));
}
