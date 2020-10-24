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
            var domain = "https://localhost:5001";
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
                SuccessUrl = domain + $"/slack/interactive/callback?viewId={Uri.EscapeDataString(viewId)}",
                CancelUrl = domain + "/slack/interactive/callback?viewId=cancel",
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
                            text="Payment received! Thank you!",
                            emoji=true
                        }
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

            return Ok();
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
                    callback_id = ""
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
                return CompleteViewAsync(payload);
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
                private_metadata = "1234",
                close = new
                {
                    type = "plain_text",
                    text = "Maybe later"
                },
                //submit = new
                //{
                //    type = "plain_text",
                //    text = "Give"
                //},
                clear_on_close = true,
                blocks = new object[]
                {
                    new
                    {
                        type = "section",
                        text = new
                        {
                            type = "plain_text",
                            text = @"In August, Ryman starts working with its next charity partners. The emphasis is on helping Melanoma New Zealand and the Melanoma Institute Australia.

Melanoma New Zealand Chief Executive Andrea Newland said the organisation was thrilled to have been chosen as a charity partner. “Ryman Healthcare’s extraordinarily generous support of so many charities over many years is truly life changing,” she told a group of Diana Isaac Retirement Village residents.

Ms Newland said Melanoma NZ’s mission was to prevent avoidable suffering and deaths from melanoma “It sounds a lofty goal, but it’s entirely achievable – as for the most part, melanoma is both preventable as well as curable if caught early.”

Meanwhile, the statistics were scary, particularly as living in New Zealand meant the chances of getting melanoma were higher than anywhere else in the world,” she said.

“More than 4,000 New Zealanders are diagnosed with melanoma every year. More than 360 people die from melanoma every year in New Zealand – that’s higher than our road toll.” More than half of all registered incidences of melanoma occurred in people aged 65 and over. Twice as many men than women died from melanoma, Ms Newland added.

Ryman Healthcare’s support would enable the charity to do some new and exciting work across New Zealand to help raise awareness about melanoma prevention and early detection, and to ultimately save lives, she said.",
                            emoji = true
                        }
                    },
                    new
                    {
                        type = "section",
                        text = new
                        {
                            type = "mrkdwn",
                            text = "*Would you like to help?*"
                        }
                    },
                    new
                    {
                        type = "actions",
                        block_id="donate_value_block",
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
                                    text = "Custom",
                                    emoji = true
                                },
                                value = "custom",
                                action_id = "donate-custom"
                            }
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

        private async Task<IActionResult> UpdateViewAsync(string payload)
        {
            var payloadObj = JsonConvert.DeserializeAnonymousType(
                payload, new
                {
                    view = new
                    {
                        id = ""
                    },
                    actions = new []
                    {
                        new
                        {
                            action_id = "",
                            value=""
                        }
                    }
                });

            if (!payloadObj.actions[0].action_id.StartsWith("donate"))
            {
                return Ok();
            }

            var donateValue = payloadObj.actions[0].value;
            var confirmationText = donateValue == "custom" ? "type" : "confirm";

            var view = new
            {
                type = "modal",
                title = new
                {
                    type = "plain_text",
                    text = "Give a Little"
                },
                callback_id = Guid.NewGuid().ToString(),
                private_metadata = "1234",
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
                        type = "section",
                        text = new
                        {
                            type = "plain_text",
                            text = @"In August, Ryman starts working with its next charity partners. The emphasis is on helping Melanoma New Zealand and the Melanoma Institute Australia.

Melanoma New Zealand Chief Executive Andrea Newland said the organisation was thrilled to have been chosen as a charity partner. “Ryman Healthcare’s extraordinarily generous support of so many charities over many years is truly life changing,” she told a group of Diana Isaac Retirement Village residents.

Ms Newland said Melanoma NZ’s mission was to prevent avoidable suffering and deaths from melanoma “It sounds a lofty goal, but it’s entirely achievable – as for the most part, melanoma is both preventable as well as curable if caught early.”

Meanwhile, the statistics were scary, particularly as living in New Zealand meant the chances of getting melanoma were higher than anywhere else in the world,” she said.

“More than 4,000 New Zealanders are diagnosed with melanoma every year. More than 360 people die from melanoma every year in New Zealand – that’s higher than our road toll.” More than half of all registered incidences of melanoma occurred in people aged 65 and over. Twice as many men than women died from melanoma, Ms Newland added.

Ryman Healthcare’s support would enable the charity to do some new and exciting work across New Zealand to help raise awareness about melanoma prevention and early detection, and to ultimately save lives, she said.",
                            emoji = true
                        }
                    },
                    new
                    {
                        type = "section",
                        text = new
                        {
                            type = "mrkdwn",
                            text = "*Would you like to help?*"
                        }
                    },
                    new
                    {
                        type = "actions",
                        block_id="donate_value_block",
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
                                    text = "Custom",
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
                        block_id="donate_amount_block",
                        element=new
                        {
                            type="plain_text_input",
                            action_id="donate_amount",
                            initial_value=donateValue=="custom"?"":donateValue,
                            placeholder=new
                            {
                                type="plain_text",
                                text="Amount to give"
                            }
                        },
                        label=new
                        {
                            type="plain_text",
                            text=$"Please {confirmationText} amount to give:",
                            emoji=false
                        }
                    },
                },
            };

            var slackRequest = new
            {
                view_id= payloadObj.view.id,
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

            await Task.Yield();

            return Ok(new
            {
                response_action = "update",
                view = new
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
                                action_id="button-action",
                                value = "click_me_123",
                                url = $"https://localhost:5001/slack/interactive/checkout?viewId={Uri.EscapeDataString(payloadObj.view.id)}" +
                                      $"&price={payloadObj.view.state.values.donate_amount_block.donate_amount.value}"
                            }
                        },
                    }
                }
            });
        }
    }
}
