using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class PayPalSubscriptionApiServiceTests
{
    [Test]
    public async Task GetActivePlansAsync_PaginatesHydratesAndUsesBillingCyclesAsTerms()
    {
        var handler = new RoutingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/oauth2/token")
            {
                return Json(HttpStatusCode.OK, """{"access_token":"access-token"}""");
            }

            if (request.RequestUri.AbsolutePath == "/v1/billing/plans")
            {
                var page = GetQueryValue(request.RequestUri, "page");
                return page == "1"
                    ? Json(HttpStatusCode.OK, """
                        {
                          "plans": [
                            { "id": "P-ACTIVE-1", "status": "ACTIVE" },
                            { "id": "P-INACTIVE", "status": "INACTIVE" }
                          ],
                          "total_pages": 2
                        }
                        """)
                    : Json(HttpStatusCode.OK, """
                        {
                          "plans": [{ "id": "P-ACTIVE-2", "status": "ACTIVE" }],
                          "total_pages": 2
                        }
                        """);
            }

            if (request.RequestUri.AbsolutePath == "/v1/billing/plans/P-ACTIVE-1")
            {
                return Json(HttpStatusCode.OK, NoTrialPlanJson(
                    "P-ACTIVE-1",
                    "Misleading name says $2.99",
                    "0.99"));
            }

            if (request.RequestUri.AbsolutePath == "/v1/billing/plans/P-ACTIVE-2")
            {
                return Json(HttpStatusCode.OK, TrialPlanJson("P-ACTIVE-2"));
            }

            throw new AssertionException($"Unexpected request: {request.Method} {request.RequestUri}");
        });
        var service = CreateService(handler);

        var plans = await service.GetActivePlansAsync();

        Assert.Multiple(() =>
        {
            Assert.That(plans, Has.Count.EqualTo(2));
            Assert.That(plans[0].Id, Is.EqualTo("P-ACTIVE-1"));
            Assert.That(plans[0].RegularPrice, Is.EqualTo(0.99m));
            Assert.That(plans[0].CurrencyCode, Is.EqualTo("USD"));
            Assert.That(plans[0].IntervalUnit, Is.EqualTo(PayPalBillingIntervals.Month));
            Assert.That(plans[0].IntervalCount, Is.EqualTo(1));
            Assert.That(plans[0].HasFreeTrial, Is.False);
            Assert.That(plans[1].HasFreeTrial, Is.True);
            Assert.That(plans[1].TrialDays, Is.EqualTo(3));
            Assert.That(handler.Requests.Count(request => request.Path == "/v1/billing/plans"), Is.EqualTo(2));
            Assert.That(handler.Requests.Any(request => request.Path.Contains("P-INACTIVE", StringComparison.Ordinal)), Is.False);
            Assert.That(handler.Requests.Where(request => request.Path == "/v1/billing/plans")
                .All(request => request.Query.Contains("page_size=20", StringComparison.Ordinal)), Is.True);
        });
    }

    [Test]
    public async Task GetSubscriptionAsync_DerivesActiveTrialAndProviderConfirmedEnd()
    {
        var handler = new RoutingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/oauth2/token")
            {
                return Json(HttpStatusCode.OK, """{"access_token":"access-token"}""");
            }

            if (request.RequestUri.AbsolutePath == "/v1/billing/subscriptions/I-TRIAL")
            {
                return Json(HttpStatusCode.OK, """
                    {
                      "id": "I-TRIAL",
                      "plan_id": "P-TRIAL",
                      "status": "ACTIVE",
                      "start_time": "2030-01-01T12:00:00Z",
                      "billing_info": {
                        "next_billing_time": "2030-01-04T12:00:05Z",
                        "cycle_executions": [
                          { "tenure_type": "TRIAL", "sequence": 1, "cycles_completed": 0, "cycles_remaining": 1, "total_cycles": 1 },
                          { "tenure_type": "REGULAR", "sequence": 2, "cycles_completed": 0, "cycles_remaining": 0, "total_cycles": 0 }
                        ]
                      },
                      "plan": {
                        "id": "P-TRIAL",
                        "name": "3 Day Trial",
                        "status": "ACTIVE",
                        "billing_cycles": [
                          {
                            "frequency": { "interval_unit": "DAY", "interval_count": 3 },
                            "tenure_type": "TRIAL",
                            "sequence": 1,
                            "total_cycles": 1,
                            "pricing_scheme": { "fixed_price": { "value": "0.00", "currency_code": "USD" } }
                          },
                          {
                            "frequency": { "interval_unit": "MONTH", "interval_count": 1 },
                            "tenure_type": "REGULAR",
                            "sequence": 2,
                            "total_cycles": 0,
                            "pricing_scheme": { "fixed_price": { "value": "0.99", "currency_code": "USD" } }
                          }
                        ]
                      }
                    }
                    """);
            }

            throw new AssertionException($"Unexpected request: {request.Method} {request.RequestUri}");
        });
        var service = CreateService(handler);

        var details = await service.GetSubscriptionAsync("I-TRIAL");

        Assert.Multiple(() =>
        {
            Assert.That(details.Id, Is.EqualTo("I-TRIAL"));
            Assert.That(details.PlanId, Is.EqualTo("P-TRIAL"));
            Assert.That(details.Status, Is.EqualTo("ACTIVE"));
            Assert.That(details.LastPaymentTime, Is.Null);
            Assert.That(details.IsInTrial, Is.True);
            Assert.That(details.TrialEndTime, Is.EqualTo(DateTimeOffset.Parse("2030-01-04T12:00:05Z")));
            Assert.That(details.Plan.RegularPrice, Is.EqualTo(0.99m));
            Assert.That(details.Plan.TrialDays, Is.EqualTo(3));
            Assert.That(details.CycleExecutions, Has.Count.EqualTo(2));
            Assert.That(handler.Requests.Single(request => request.Path == "/v1/billing/subscriptions/I-TRIAL").Query,
                Does.Contain("fields=plan"));
        });
    }

    [Test]
    public async Task GetSubscriptionAsync_DoesNotTreatConvertedSubscriptionAsTrial()
    {
        var handler = new RoutingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/oauth2/token")
            {
                return Json(HttpStatusCode.OK, """{"access_token":"access-token"}""");
            }

            return Json(HttpStatusCode.OK, """
                {
                  "id": "I-PAID",
                  "plan_id": "P-TRIAL",
                  "status": "ACTIVE",
                  "start_time": "2030-01-01T12:00:00Z",
                  "billing_info": {
                    "next_billing_time": "2030-02-04T12:00:00Z",
                    "last_payment": { "time": "2030-01-04T12:00:00Z" },
                    "cycle_executions": [
                      { "tenure_type": "TRIAL", "sequence": 1, "cycles_completed": 1, "cycles_remaining": 0, "total_cycles": 1 },
                      { "tenure_type": "REGULAR", "sequence": 2, "cycles_completed": 1, "cycles_remaining": 0, "total_cycles": 0 }
                    ]
                  },
                  "plan": {
                    "id": "P-TRIAL",
                    "status": "ACTIVE",
                    "billing_cycles": [
                      {
                        "frequency": { "interval_unit": "DAY", "interval_count": 3 },
                        "tenure_type": "TRIAL", "sequence": 1, "total_cycles": 1,
                        "pricing_scheme": { "fixed_price": { "value": "0.00", "currency_code": "USD" } }
                      },
                      {
                        "frequency": { "interval_unit": "MONTH", "interval_count": 1 },
                        "tenure_type": "REGULAR", "sequence": 2, "total_cycles": 0,
                        "pricing_scheme": { "fixed_price": { "value": "0.99", "currency_code": "USD" } }
                      }
                    ]
                  }
                }
                """);
        });
        var service = CreateService(handler);

        var details = await service.GetSubscriptionAsync("I-PAID");

        Assert.Multiple(() =>
        {
            Assert.That(details.IsInTrial, Is.False);
            Assert.That(details.LastPaymentTime, Is.EqualTo(DateTimeOffset.Parse("2030-01-04T12:00:00Z")));
            Assert.That(details.TrialEndTime, Is.EqualTo(DateTimeOffset.Parse("2030-01-04T12:00:00Z")));
        });
    }

    [Test]
    public async Task GetSubscriptionAsync_DoesNotInventTrialForCancelledUnactivatedCheckout()
    {
        var handler = new RoutingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/oauth2/token")
            {
                return Json(HttpStatusCode.OK, """{"access_token":"access-token"}""");
            }

            return Json(HttpStatusCode.OK, """
                {
                  "id": "I-ABANDONED",
                  "plan_id": "P-TRIAL",
                  "status": "CANCELLED",
                  "start_time": "2030-01-01T12:00:00Z",
                  "billing_info": {},
                  "plan": {
                    "id": "P-TRIAL",
                    "status": "ACTIVE",
                    "billing_cycles": [
                      {
                        "frequency": { "interval_unit": "DAY", "interval_count": 3 },
                        "tenure_type": "TRIAL", "sequence": 1, "total_cycles": 1,
                        "pricing_scheme": { "fixed_price": { "value": "0.00", "currency_code": "USD" } }
                      },
                      {
                        "frequency": { "interval_unit": "MONTH", "interval_count": 1 },
                        "tenure_type": "REGULAR", "sequence": 2, "total_cycles": 0,
                        "pricing_scheme": { "fixed_price": { "value": "0.99", "currency_code": "USD" } }
                      }
                    ]
                  }
                }
                """);
        });
        var service = CreateService(handler);

        var details = await service.GetSubscriptionAsync("I-ABANDONED");

        Assert.Multiple(() =>
        {
            Assert.That(details.IsInTrial, Is.False);
            Assert.That(details.TrialEndTime, Is.Null);
            Assert.That(details.LastPaymentTime, Is.Null);
        });
    }

    [Test]
    public async Task CreateSubscriptionAsync_SendsChosenPlanAndReturnsApprovalLink()
    {
        var handler = new RoutingHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/oauth2/token")
            {
                Assert.That(request.Headers.Authorization?.Scheme, Is.EqualTo("Basic"));
                Assert.That(body, Does.Contain("grant_type=client_credentials"));
                return Json(HttpStatusCode.OK, """{"access_token":"access-token"}""");
            }

            Assert.Multiple(() =>
            {
                Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
                Assert.That(request.RequestUri.AbsolutePath, Is.EqualTo("/v1/billing/subscriptions"));
                Assert.That(request.Headers.Authorization, Is.EqualTo(new AuthenticationHeaderValue("Bearer", "access-token")));
                Assert.That(request.Headers.GetValues("Prefer"), Does.Contain("return=representation"));
            });

            using var document = JsonDocument.Parse(body);
            Assert.Multiple(() =>
            {
                Assert.That(document.RootElement.GetProperty("plan_id").GetString(), Is.EqualTo("P-SELECTED"));
                Assert.That(document.RootElement.GetProperty("application_context").GetProperty("return_url").GetString(),
                    Is.EqualTo("https://streamtunes.example/manage-account?success=true"));
                Assert.That(document.RootElement.GetProperty("application_context").GetProperty("cancel_url").GetString(),
                    Is.EqualTo("https://streamtunes.example/manage-account?success=false"));
            });

            return Json(HttpStatusCode.Created, """
                {
                  "id": "I-NEW",
                  "links": [
                    { "href": "https://www.paypal.com/approve/I-NEW", "rel": "approve", "method": "GET" }
                  ]
                }
                """);
        });
        var service = CreateService(handler);

        var result = await service.CreateSubscriptionAsync(
            "P-SELECTED",
            "https://streamtunes.example/manage-account?success=true",
            "https://streamtunes.example/manage-account?success=false");

        Assert.Multiple(() =>
        {
            Assert.That(result.Id, Is.EqualTo("I-NEW"));
            Assert.That(result.ApprovalUrl, Is.EqualTo("https://www.paypal.com/approve/I-NEW"));
        });
    }

    [Test]
    public async Task CancelSubscriptionAsync_TreatsResourceNotFoundAsAlreadyCancelled()
    {
        var handler = new RoutingHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/oauth2/token")
            {
                return Json(HttpStatusCode.OK, """{"access_token":"access-token"}""");
            }

            using var document = JsonDocument.Parse(body);
            Assert.That(document.RootElement.GetProperty("reason").GetString(), Is.EqualTo("Cancelled during trial"));
            return Json(HttpStatusCode.NotFound, """
                { "name": "RESOURCE_NOT_FOUND", "message": "The specified resource does not exist." }
                """);
        });
        var service = CreateService(handler);

        var result = await service.CancelSubscriptionAsync("I-MISSING", "Cancelled during trial");

        Assert.That(result, Is.True);
    }

    [Test]
    public void GetActivePlansAsync_WrapsOAuthNetworkFailure()
    {
        var handler = new RoutingHandler((_, _) =>
            throw new HttpRequestException("Simulated connection failure"));
        var service = CreateService(handler);

        var exception = Assert.ThrowsAsync<PayPalSubscriptionApiException>(async () =>
            await service.GetActivePlansAsync());

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("network error"));
            Assert.That(exception.InnerException, Is.InstanceOf<HttpRequestException>());
        });
    }

    [Test]
    public void GetPlanAsync_WrapsApiTimeout()
    {
        var handler = new RoutingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/oauth2/token")
            {
                return Json(HttpStatusCode.OK, """{"access_token":"access-token"}""");
            }

            throw new TaskCanceledException("Simulated PayPal timeout");
        });
        var service = CreateService(handler);

        var exception = Assert.ThrowsAsync<PayPalSubscriptionApiException>(async () =>
            await service.GetPlanAsync("P-TIMEOUT"));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("timed out"));
            Assert.That(exception.InnerException, Is.InstanceOf<TaskCanceledException>());
        });
    }

    [Test]
    public void GetActivePlansAsync_PreservesCallerCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var service = CreateService(new CallerCancellationHandler());

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await service.GetActivePlansAsync(cancellationSource.Token));
    }

    private static PayPalSubscriptionApiService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["PayPal:ClientId"] = "client-id",
                ["PayPal:Secret"] = "client-secret",
                ["PayPal:ApiBaseUrl"] = "https://api-m.sandbox.paypal.com/"
            })
            .Build();
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(value => value.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        return new PayPalSubscriptionApiService(
            configuration,
            factory.Object,
            NullLogger<PayPalSubscriptionApiService>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static string GetQueryValue(Uri uri, string name)
    {
        return uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .First(parts => string.Equals(parts[0], name, StringComparison.Ordinal))[1];
    }

    private static string NoTrialPlanJson(string id, string name, string price)
    {
        return $$"""
            {
              "id": "{{id}}",
              "product_id": "PROD-STREAMTUNES",
              "name": "{{name}}",
              "status": "ACTIVE",
              "billing_cycles": [
                {
                  "frequency": { "interval_unit": "MONTH", "interval_count": 1 },
                  "tenure_type": "REGULAR",
                  "sequence": 1,
                  "total_cycles": 0,
                  "pricing_scheme": { "fixed_price": { "value": "{{price}}", "currency_code": "USD" } }
                }
              ]
            }
            """;
    }

    private static string TrialPlanJson(string id)
    {
        return $$"""
            {
              "id": "{{id}}",
              "product_id": "PROD-STREAMTUNES",
              "name": "3 days free then $0.99",
              "status": "ACTIVE",
              "billing_cycles": [
                {
                  "frequency": { "interval_unit": "DAY", "interval_count": 3 },
                  "tenure_type": "TRIAL",
                  "sequence": 1,
                  "total_cycles": 1,
                  "pricing_scheme": { "fixed_price": { "value": "0.00", "currency_code": "USD" } }
                },
                {
                  "frequency": { "interval_unit": "MONTH", "interval_count": 1 },
                  "tenure_type": "REGULAR",
                  "sequence": 2,
                  "total_cycles": 0,
                  "pricing_scheme": { "fixed_price": { "value": "0.99", "currency_code": "USD" } }
                }
              ]
            }
            """;
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _route;

        public RoutingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> route)
        {
            _route = route;
        }

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.RequestUri!.AbsolutePath, request.RequestUri.Query));
            return _route(request, body);
        }
    }

    private sealed class CallerCancellationHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }
    }

    private sealed record CapturedRequest(string Path, string Query);
}
