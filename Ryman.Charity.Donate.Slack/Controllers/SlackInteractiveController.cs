using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Stripe;
using Stripe.Checkout;

namespace Ryman.Charity.Donate.Slack.Controllers
{
    [ApiController]
    [Route("slack/interactive")]
    public class SlackInteractiveController : ControllerBase
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _configuration;

        private static readonly string Domain = "https://57d97136aa76.ngrok.io";

        public SlackInteractiveController(IHttpClientFactory httpFactory, IConfiguration configuration)
        {
            _httpFactory = httpFactory;
            _configuration = configuration;
        }

        [HttpGet]
        public IActionResult Get()
        {
            return Ok("You are OK!");
        }

        [HttpGet]
        [Route("checkout")]
        public IActionResult Checkout([FromQuery] long price, string viewId)
        {
            // https://stripe.com/docs/checkout/integration-builder
            var options = new SessionCreateOptions
            {
                PaymentMethodTypes = new List<string>
                {
                    "card",
                },
                LineItems = new List<SessionLineItemOptions>
                {
                    new SessionLineItemOptions
                    {
                        PriceData = new SessionLineItemPriceDataOptions
                        {
                            UnitAmount = price *100,
                            Currency = "nzd",
                            ProductData = new SessionLineItemPriceDataProductDataOptions
                            {
                                Name = "Donation",
                            },
                        },
                        Quantity = 1,
                    },
                },
                Mode = "payment",
                SuccessUrl = Domain + $"/slack/interactive/callback?viewId={Uri.EscapeDataString(viewId)}",
                CancelUrl = Domain + "/slack/interactive/callback?viewId=cancel",
            };
            var service = new SessionService();
            Session session = service.Create(options);

            var env = HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>();
            var checkout = System.IO.File.ReadAllText(Path.Combine(env.WebRootPath, "checkout.html"));

            checkout = checkout.Replace(
                    "$apikey$",
                    _configuration.GetSection("Stripe:PublishableKey").Value)
                .Replace("$sessionId$", session.Id);

            return Content(checkout, "text/html", Encoding.UTF8);
        }

        [HttpGet]
        [Route("callback")]
        public async Task<IActionResult> Callback([FromQuery] string viewId)
        {
            if (viewId == "cancel")
            {
                return Ok();
            }

            var view = new
            {
                type = "modal",
                title = new
                {
                    type = "plain_text",
                    text = "Thank you!",
                    emoji = true
                },
                close = new
                {
                    type = "plain_text",
                    text = "Close",
                    emoji = false,
                },
                blocks = new object[]
                {
                    new
                    {
                        type="section",
                        text=new
                        {
                            type="plain_text",
                            text="Payment received!",
                            emoji=true
                        }
                    },
                    new
                    {
                        type="image",
                        image_url="https://media.tenor.com/images/08c9b2fc65a8ff8e6e72a65910138b9a/tenor.gif",
                        alt_text="Thank you"
                    }
                }
            };

            var slackRequest = new
            {
                view_id = viewId,
                view
            };

            var body = JsonConvert.SerializeObject(slackRequest);

            using (var client = _httpFactory.CreateClient("slack"))
            {
                var postResponse = await client.PostAsync("https://slack.com/api/views.update",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                var content = await postResponse.Content.ReadAsStringAsync();
            }

            return Ok("Your payment has been processed successfully. Feel free to close this browser window.");
        }

        [HttpPost]
        public Task<IActionResult> Post([FromForm] string payload)
        {
            var response = JsonConvert.DeserializeAnonymousType(
                payload,
                new
                {
                    trigger_id = "",
                    type = "",
                    callback_id = "",
                    view = new
                    {
                        private_metadata = ""
                    }
                });

            if (response.type == "shortcut" && response.callback_id == "ryman_charity_donate")
            {
                return StartViewAsync(response.trigger_id);
            }
            
            if (response.type == "block_actions")
            {
                return UpdateViewAsync(payload);
            }

            if (response.type == "view_submission")
            {
                if (string.Equals(response.view?.private_metadata, "SummaryView"))
                {
                    return StartDonateViewAsync();
                }
                else
                {
                    return CompleteViewAsync(payload);
                }
            }

            return Task.FromResult<IActionResult>(Ok());
        }

        private async Task<IActionResult> StartViewAsync(string trigger_id)
        {
            var view = new
            {
                type = "modal",
                title = new
                {
                    type = "plain_text",
                    text = "Give a Little"
                },
                callback_id = Guid.NewGuid().ToString(),
                private_metadata = "SummaryView",
                close = new
                {
                    type = "plain_text",
                    text = "Maybe later"
                },
                submit = new
                {
                    type = "plain_text",
                    text = "Donate"
                },
                clear_on_close = true,
                blocks = new object[]
                {
                    new
                    {
                        type = "image",
                        image_url = "https://novashades.co.nz/wp-content/uploads/2018/10/BLOG-POST_MELANOMA-750x200.jpg",
                        alt_text = "Melanoma New Zealand"
                    },
                    new
                    {
                        type = "section",
                        text=new
                        {
                            type="mrkdwn",
                            text="*Melanoma New Zealand is the only charity organisation dedicated to preventing avoidable deaths and suffering from melanoma, by:*"
                        }
                    },
                    new
                    {
                        type = "section",
                        text=new
                        {
                            type="mrkdwn",
                            text="• Providing information about all aspects of melanoma\r\n• Promoting regular skin checks for early detection\r\n• Advocating for increased access to high quality clinical care\r\n• Leveraging relationships to amplify our effectiveness\r\n• Being financially sustainable to achieve our mission"
                        }
                    },
                    new
                    {
                        type = "section",
                        text=new
                        {
                            type="mrkdwn",
                            text="*If melanoma is recognised and treated early enough it is almost always curable.*"
                        }
                    }
                },
            };

            var slackRequest = new
            {
                trigger_id,
                view
            };

            var body = JsonConvert.SerializeObject(slackRequest);

            using (var client = _httpFactory.CreateClient("slack"))
            {
                var postResponse = await client.PostAsync("https://slack.com/api/views.open",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                var content = await postResponse.Content.ReadAsStringAsync();
            }

            return Ok();
        }

        private async Task<IActionResult> StartDonateViewAsync()
        {
            var view = new
            {
                type = "modal",
                title = new
                {
                    type = "plain_text",
                    text = "Give a Little"
                },
                callback_id = Guid.NewGuid().ToString(),
                private_metadata = "DonateView",
                close = new
                {
                    type = "plain_text",
                    text = "Maybe later"
                },
                clear_on_close = true,
                blocks = new object[]
                {
                    new
                    {
                        type = "image",
                        image_url = "https://novashades.co.nz/wp-content/uploads/2018/10/BLOG-POST_MELANOMA-750x200.jpg",
                        alt_text = "Melanoma New Zealand"
                    },
                    new
                    {
                        type = "section",
                        text = new
                        {
                            type = "mrkdwn",
                            text = "*Here are some handy ideas and tips to get you started:*"
                        }
                    },
                    new
                    {
                        type="divider"
                    },
                    new
                    {
                        type = "section",
                        text = new
                        {
                            type = "mrkdwn",
                            text = "*Pay via chattR*"
                        }
                    },
                    new
                    {
                        type = "section",
                        text = new
                        {
                            type = "mrkdwn",
                            text = "Simply choose your amount to give and complete payment via chattR."
                        }
                    },
                    new
                    {
                        type = "actions",
                        block_id = "donate_value_block",
                        elements = new object[]
                        {
                            new
                            {
                                type = "button",
                                text = new
                                {
                                    type = "plain_text",
                                    text = "$5",
                                    emoji = true
                                },
                                value = "5",
                                action_id = "donate-5"
                            },
                            new
                            {
                                type = "button",
                                text = new
                                {
                                    type = "plain_text",
                                    text = "$10",
                                    emoji = true
                                },
                                value = "10",
                                action_id = "donate-10"
                            },
                            new
                            {
                                type = "button",
                                text = new
                                {
                                    type = "plain_text",
                                    text = "$20",
                                    emoji = true
                                },
                                value = "20",
                                action_id = "donate-20"
                            },
                            new
                            {
                                type = "button",
                                text = new
                                {
                                    type = "plain_text",
                                    text = "More or less",
                                    emoji = true
                                },
                                value = "custom",
                                action_id = "donate-custom"
                            }
                        }
                    },
                    new
                    {
                        type="divider"
                    },
                    new
                    {
                        type = "section",
                        text = new
                        {
                            type = "mrkdwn",
                            text = "*Talk to staff at your local reception*"
                        }
                    },
                    new
                    {
                        type = "section",
                        text = new
                        {
                            type = "mrkdwn",
                            text = "You can pay with cash or card at reception, or bank transfer direct to the charity account:"
                        }
                    },
                    new
                    {
                        type = "section",
                        text = new
                        {
                            type = "mrkdwn",
                            text = "*Bank Account Name:* Ryman Charity Bank Account\r\n*Bank Account Number:* 01-1111-2222222-00"
                        }
                    },
                },
            };

            await Task.Yield();

            return Ok(new
            {
                response_action = "update",
                view
            });
        }

        private async Task<IActionResult> UpdateViewAsync(string payload)
        {
            var payloadObj = JsonConvert.DeserializeAnonymousType(
                payload, new
                {
                    view = new
                    {
                        id = ""
                    },
                    actions = new[]
                    {
                        new
                        {
                            action_id = "",
                            value = ""
                        }
                    }
                });

            if (!payloadObj.actions[0].action_id.StartsWith("donate"))
            {
                return Ok();
            }

            var donateValue = payloadObj.actions[0].value;
            var confirmationText = donateValue == "custom" ? "type" : "confirm";

            object view;
            if (donateValue != "custom")
            {
                view = CreatePayView(payloadObj.view.id, donateValue);
            }
            else
            {
                view = new
                {
                    type = "modal",
                    title = new
                    {
                        type = "plain_text",
                        text = "Give a Little"
                    },
                    callback_id = Guid.NewGuid().ToString(),
                    private_metadata = "DonateView",
                    close = new
                    {
                        type = "plain_text",
                        text = "Maybe later"
                    },
                    submit = new
                    {
                        type = "plain_text",
                        text = "Give"
                    },
                    clear_on_close = true,
                    blocks = new object[]
                    {
                        new
                        {
                            type = "image",
                            image_url =
                                "https://novashades.co.nz/wp-content/uploads/2018/10/BLOG-POST_MELANOMA-750x200.jpg",
                            alt_text = "Melanoma New Zealand"
                        },
                        new
                        {
                            type = "section",
                            text = new
                            {
                                type = "mrkdwn",
                                text = "*Here are some handy ideas and tips to get you started:*"
                            }
                        },
                        new
                        {
                            type = "divider"
                        },
                        new
                        {
                            type = "section",
                            text = new
                            {
                                type = "mrkdwn",
                                text = "*Pay via chattR*"
                            }
                        },
                        new
                        {
                            type = "section",
                            text = new
                            {
                                type = "mrkdwn",
                                text = "Simply choose your amount to give and complete payment via chattR."
                            }
                        },
                        new
                        {
                            type = "actions",
                            block_id = "donate_value_block",
                            elements = new object[]
                            {
                                new
                                {
                                    type = "button",
                                    text = new
                                    {
                                        type = "plain_text",
                                        text = "$5",
                                        emoji = true
                                    },
                                    value = "5",
                                    action_id = "donate-5"
                                },
                                new
                                {
                                    type = "button",
                                    text = new
                                    {
                                        type = "plain_text",
                                        text = "$10",
                                        emoji = true
                                    },
                                    value = "10",
                                    action_id = "donate-10"
                                },
                                new
                                {
                                    type = "button",
                                    text = new
                                    {
                                        type = "plain_text",
                                        text = "$20",
                                        emoji = true
                                    },
                                    value = "20",
                                    action_id = "donate-20"
                                },
                                new
                                {
                                    type = "button",
                                    text = new
                                    {
                                        type = "plain_text",
                                        text = "More or less",
                                        emoji = true
                                    },
                                    value = "custom",
                                    action_id = "donate-custom"
                                }
                            }
                        },
                        new
                        {
                            type = "input",
                            block_id = "donate_amount_block",
                            element = new
                            {
                                type = "plain_text_input",
                                action_id = "donate_amount",
                                initial_value = donateValue == "custom" ? "" : donateValue,
                                placeholder = new
                                {
                                    type = "plain_text",
                                    text = "Amount to give"
                                }
                            },
                            label = new
                            {
                                type = "plain_text",
                                text = $"Please {confirmationText} amount to give:",
                                emoji = false
                            }
                        },
                        new
                        {
                            type = "divider"
                        },
                        new
                        {
                            type = "section",
                            text = new
                            {
                                type = "mrkdwn",
                                text = "*Talk to staff at your local reception*"
                            }
                        },
                        new
                        {
                            type = "section",
                            text = new
                            {
                                type = "mrkdwn",
                                text =
                                    "You can pay with cash or card at reception, or bank transfer direct to the charity account:"
                            }
                        },
                        new
                        {
                            type = "section",
                            text = new
                            {
                                type = "mrkdwn",
                                text =
                                    "*Bank Account Name:* Ryman Charity Bank Account\r\n*Bank Account Number:* 01-1111-2222222-00"
                            }
                        },
                    },
                };
            }

            var slackRequest = new
            {
                view_id = payloadObj.view.id,
                view
            };

            var body = JsonConvert.SerializeObject(slackRequest);

            using (var client = _httpFactory.CreateClient("slack"))
            {
                var postResponse = await client.PostAsync("https://slack.com/api/views.update",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                var content = await postResponse.Content.ReadAsStringAsync();
            }

            return Ok();
        }

        private object CreatePayView(string viewId, string amount)
        {
            return new
            {
                type = "modal",
                title = new
                {
                    type = "plain_text",
                    text = "One step to go",
                    emoji = true
                },
                close = new
                {
                    type = "plain_text",
                    text = "Close",
                    emoji = false,
                },
                blocks = new object[]
                {
                    new
                    {
                        type = "section",
                        text = new
                        {
                            type = "mrkdwn",
                            text = "Click \"Pay\" and complete payment in your browser window."
                        },
                        accessory = new
                        {
                            type = "button",
                            text = new
                            {
                                type = "plain_text",
                                text = "Pay",
                                emoji = true
                            },
                            action_id = "button-action",
                            value = "click_me_123",
                            url = $"{Domain}/slack/interactive/checkout?viewId={Uri.EscapeDataString(viewId)}" +
                                  $"&price={amount}"
                        }
                    },
                }
            };
        }

        private async Task<IActionResult> CompleteViewAsync(string payload)
        {
            var payloadObj = JsonConvert.DeserializeAnonymousType(
                payload, new
                {
                    view = new
                    {
                        id = "",
                        state = new
                        {
                            values = new
                            {
                                donate_amount_block = new
                                {
                                    donate_amount = new
                                    {
                                        value = ""
                                    }
                                }
                            }
                        }
                    }
                });

            var view = CreatePayView(
                payloadObj.view.id,
                payloadObj.view.state.values.donate_amount_block.donate_amount.value);

            await Task.Yield();

            return Ok(new
            {
                response_action = "update",
                view
            });
        }
    }
}
