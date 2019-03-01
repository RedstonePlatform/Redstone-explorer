﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PaulMiami.AspNetCore.Mvc.Recaptcha;
using QRCoder;
using RestSharp;
using Stratis.Guru.Models;
using Stratis.Guru.Modules;
using Stratis.Guru.Settings;

namespace Stratis.Guru.Controllers
{
    public class HomeController : Controller
    {
        private readonly IMemoryCache _memoryCache;
        private readonly IAsk _ask;
        private readonly ISettings _settings;
        private readonly IParticipation _participation;
        private readonly IDraws _draws;
        private readonly DrawSettings _drawSettings;
        private readonly SetupSettings _setupSettings;
        private readonly FeaturesSettings _featuresSettings;
        private readonly ColdStakingSettings _coldStakingSettings;

        public HomeController(IMemoryCache memoryCache, 
            IAsk ask, 
            ISettings settings, 
            IParticipation participation, 
            IDraws draws, 
            IOptions<DrawSettings> drawSettings, 
            IOptions<SetupSettings> setupSettings,
            IOptions<FeaturesSettings> featuresSettings,
            IOptions<ColdStakingSettings> coldStakingSettings)
        {
            _memoryCache = memoryCache;
            _ask = ask;
            _settings = settings;
            _participation = participation;
            _draws = draws;
            _drawSettings = drawSettings.Value;
            _setupSettings = setupSettings.Value;
            _featuresSettings = featuresSettings.Value;
            _coldStakingSettings = coldStakingSettings.Value;
        }
        
        public IActionResult Index()
        {
            ViewBag.Features = _featuresSettings;
            ViewBag.Setup = _setupSettings;

            double displayPrice = 0;
            var rqf = Request.HttpContext.Features.Get<IRequestCultureFeature>();
            dynamic coinmarketcap = JsonConvert.DeserializeObject(_memoryCache.Get("Coinmarketcap").ToString());
            var last24Change = coinmarketcap.data.quotes.USD.percent_change_24h / 100;
            
            if (rqf.RequestCulture.UICulture.Name.Equals("en-US"))
            {
                displayPrice = coinmarketcap.data.quotes.USD.price;
            }
            else
            {
                dynamic fixerApiResponse = JsonConvert.DeserializeObject(_memoryCache.Get("Fixer").ToString());
                var dollarRate = fixerApiResponse.rates.USD;
                try
                {
                    var regionInfo = new RegionInfo(rqf.RequestCulture.UICulture.Name.ToUpper());
                    var browserCurrencyRate = (double) ((JObject) fixerApiResponse.rates)[regionInfo.ISOCurrencySymbol];
                    displayPrice = 1 / (double) dollarRate * (double) coinmarketcap.data.quotes.USD.price * browserCurrencyRate;
                }
                catch
                {
                    // ignored
                }
            }
            
            return View(new Ticker
            {
                DisplayPrice = displayPrice,
                Last24Change = last24Change
            });
        }

        [Route("coldstaking-progress/{testnet?}")]
        public IActionResult ColdStakingProgress(string testnet = null)
        {
            ViewBag.Testnet = testnet;
            ViewBag.Features = _featuresSettings;
            ViewBag.Setup = _setupSettings;

            string lastColdStakingStatus;

            if (!_memoryCache.TryGetValue(testnet == null ? "cold-staking-mainnet":"cold-staking-testnet", out lastColdStakingStatus))
            {
                var client = new RestClient(testnet == null ? _coldStakingSettings.Mainnet : _coldStakingSettings.Testnet);
                var request = new RestRequest(Method.GET);
                lastColdStakingStatus = client.Execute(request).Content;
                var cacheEntryOptions = new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(5));
                _memoryCache.Set(testnet == null ? "cold-staking-mainnet" : "cold-staking-testnet", lastColdStakingStatus, cacheEntryOptions);
            }
            ViewBag.Status = (JsonConvert.DeserializeObject(lastColdStakingStatus) as dynamic)[0];

            return View();
        }

        [Route("lottery")]
        public IActionResult Lottery()
        {
            ViewBag.Features = _featuresSettings;
            ViewBag.Setup = _setupSettings;

            ViewBag.NextDraw = long.Parse(_memoryCache.Get("NextDraw").ToString());
            ViewBag.Jackpot = _memoryCache.Get("Jackpot");
            ViewBag.Players = _participation.GetPlayers(_draws.GetLastDraw());
            return View();
        }

        [ValidateRecaptcha]
        [HttpPost]
        [Route("lottery/participate")]
        public IActionResult Participate()
        {
            ViewBag.Features = _featuresSettings;
            ViewBag.Setup = _setupSettings;

            if (ModelState.IsValid)
            {
                var lastDraw = _draws.GetLastDraw();
                HttpContext.Session.SetString("HaveBeginParticipation", "true");
                return RedirectToAction("Participate", new{id=lastDraw});
            }

            ViewBag.NextDraw = long.Parse(_memoryCache.Get("NextDraw").ToString());
            ViewBag.Jackpot = _memoryCache.Get("Jackpot");
            ViewBag.Players = _participation.GetPlayers(_draws.GetLastDraw());
            ViewBag.Participate = true;
            return View("Lottery");
        }

        [Route("lottery/participate/{id}")]
        public IActionResult Participate(string id)
        {
            ViewBag.Features = _featuresSettings;
            ViewBag.Setup = _setupSettings;

            if (HttpContext.Session.GetString("HaveBeginParticipation") == null)
            {
                return RedirectToAction("Lottery");
            }
            ViewBag.NextDraw = long.Parse(_memoryCache.Get("NextDraw").ToString());
            ViewBag.Jackpot = _memoryCache.Get("Jackpot");
            ViewBag.Players = _participation.GetPlayers(_draws.GetLastDraw());
            ViewBag.Participate = true;
            
            var pubkey = ExtPubKey.Parse(_drawSettings.PublicKey);
            ViewBag.DepositAddress = pubkey.Derive(0).Derive(_settings.GetIterator()).PubKey.GetAddress(Network.StratisMain);

            return View("Lottery");
        }

        [HttpPost]
        [Route("lottery/check-payment")]
        public IActionResult CheckPayment()
        {
            ViewBag.Features = _featuresSettings;
            ViewBag.Setup = _setupSettings;

            var pubkey = ExtPubKey.Parse(_drawSettings.PublicKey);
            var depositAddress = pubkey.Derive(0).Derive(_settings.GetIterator()).PubKey.GetAddress(Network.StratisMain).ToString();
            ViewBag.DepositAddress = depositAddress;

            var rc = new RestClient($"https://stratis.guru/api/address/{depositAddress}");
            var rq = new RestRequest(Method.GET);
            var response = rc.Execute(rq);
            dynamic stratisAdressRequest = JsonConvert.DeserializeObject(response.Content);
            if(stratisAdressRequest.unconfirmedBalance + stratisAdressRequest.balance > 0)
            {
                HttpContext.Session.SetString("Deposited", depositAddress);
                HttpContext.Session.SetString("DepositedAmount", ((double)(stratisAdressRequest.unconfirmedBalance + stratisAdressRequest.balance)).ToString());
                return Json(true);
            }
            return BadRequest();
        }

        [Route("lottery/new-participation")]
        public IActionResult NewParticipation()
        {
            ViewBag.Features = _featuresSettings;
            ViewBag.Setup = _setupSettings;

            var ticket = Guid.NewGuid().ToString();
            HttpContext.Session.SetString("Ticket", ticket);
            ViewBag.Ticket = ticket;
            return PartialView();
        }

        [Route("lottery/save-participation")]
        public IActionResult SaveParticipation(string nickname, string address)
        {
            _settings.IncrementIterator();
            _participation.StoreParticipation(HttpContext.Session.GetString("Ticket"), nickname, address, double.Parse(HttpContext.Session.GetString("DepositedAmount")));

            return RedirectToAction("Participated");
        }

        [Route("lottery/participated")]
        public IActionResult Participated()
        {
            ViewBag.Features = _featuresSettings;
            ViewBag.Setup = _setupSettings;

            ViewBag.NextDraw = long.Parse(_memoryCache.Get("NextDraw").ToString());
            ViewBag.Jackpot = _memoryCache.Get("Jackpot");
            ViewBag.Players = _participation.GetPlayers(_draws.GetLastDraw());
            ViewBag.Participated = true;
            return View("Lottery");
        }

        [Route("about")]
        public IActionResult About()
        {
            ViewBag.Features = _featuresSettings;
            ViewBag.Setup = _setupSettings;

            return View();
        }

        [Route("vanity")]
        public IActionResult Vanity()
        {
            ViewBag.Features = _featuresSettings;
            ViewBag.Setup = _setupSettings;

            return View();
        }

        [HttpPost]
        [Route("vanity")]
        public IActionResult Vanity(Vanity vanity)
        {
            ViewBag.Features = _featuresSettings;
            ViewBag.Setup = _setupSettings;

            if (ModelState.IsValid)
            {
                _ask.NewVanity(vanity);
                ViewBag.Succeed = true;
            }
            return View();
        }

        [Route("generator")]
        public IActionResult Generator()
        {
            ViewBag.Features = _featuresSettings;
            ViewBag.Setup = _setupSettings;

            var stratisAddress = new Key();
            return View(new StratisAddressPayload
            {
                PrivateKey = stratisAddress.GetWif(Network.StratisMain).ToString(),
                PublicKey = stratisAddress.PubKey.GetAddress(Network.StratisMain).ToString()
            });
        }

        [Route("qr/{value}")]
        public IActionResult Qr(string value)
        {
            ViewBag.Features = _featuresSettings;
            ViewBag.Setup = _setupSettings;

            var memoryStream = new MemoryStream();
            var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(value, QRCodeGenerator.ECCLevel.L);
            var qrCode = new QRCode(qrCodeData);
            qrCode.GetGraphic(20, Color.Black, Color.White, false).Save(memoryStream, ImageFormat.Png);
            return File(memoryStream.ToArray(), "image/png");
        }

        public IActionResult Documentation()
        {
            ViewBag.Features = _featuresSettings;
            ViewBag.Setup = _setupSettings;

            return Redirect("/documentation");
        }
    }
}