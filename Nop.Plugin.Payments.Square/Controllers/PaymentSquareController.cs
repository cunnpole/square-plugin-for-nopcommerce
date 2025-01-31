﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Nop.Core;
using Nop.Plugin.Payments.Square.Domain;
using Nop.Plugin.Payments.Square.Models;
using Nop.Plugin.Payments.Square.Services;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Messages;
using Nop.Services.Security;
using Nop.Web.Framework;
using Nop.Web.Framework.Controllers;
using Nop.Web.Framework.Mvc.Filters;

namespace Nop.Plugin.Payments.Square.Controllers
{
    [AutoValidateAntiforgeryToken]
    public class PaymentSquareController : BasePaymentController
    {
        #region Fields

        private readonly ILocalizationService _localizationService;
        private readonly INotificationService _notificationService;
        private readonly IPermissionService _permissionService;
        private readonly ISettingService _settingService;
        private readonly IStoreContext _storeContext;
        private readonly SquarePaymentManager _squarePaymentManager;

        #endregion

        #region Ctor

        public PaymentSquareController(ILocalizationService localizationService,
            INotificationService notificationService,
            IPermissionService permissionService,
            ISettingService settingService,
            IStoreContext storeContext,
            SquarePaymentManager squarePaymentManager)
        {
            _localizationService = localizationService;
            _notificationService = notificationService;
            _permissionService = permissionService;
            _settingService = settingService;
            _storeContext = storeContext;
            _squarePaymentManager = squarePaymentManager;
        }

        #endregion

        #region Methods

        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public async Task<IActionResult> Configure()
        {
            if (!(await _permissionService.AuthorizeAsync(StandardPermissionProvider.ManagePaymentMethods)))
                return AccessDeniedView();

            //load settings for a chosen store scope
            var storeId = await _storeContext.GetActiveStoreScopeConfigurationAsync();
            var settings = await _settingService.LoadSettingAsync<SquarePaymentSettings>(storeId);

            //prepare model
            var model = new ConfigurationModel
            {
                ApplicationSecret = settings.ApplicationSecret,
                UseSandbox = settings.UseSandbox,
                Use3ds = settings.Use3ds,
                TransactionModeId = (int)settings.TransactionMode,
                LocationId = settings.LocationId,
                AdditionalFee = settings.AdditionalFee,
                AdditionalFeePercentage = settings.AdditionalFeePercentage,
                ActiveStoreScopeConfiguration = storeId
            };
            if (model.UseSandbox)
            {
                model.SandboxApplicationId = settings.ApplicationId;
                model.SandboxAccessToken = settings.AccessToken;
            }
            else
            {
                model.ApplicationId = settings.ApplicationId;
                model.AccessToken = settings.AccessToken;
            }

            if (storeId > 0)
            {
                model.UseSandbox_OverrideForStore = await _settingService.SettingExistsAsync(settings, x => x.UseSandbox, storeId);
                model.Use3ds_OverrideForStore = await _settingService.SettingExistsAsync(settings, x => x.Use3ds, storeId);
                model.TransactionModeId_OverrideForStore = await _settingService.SettingExistsAsync(settings, x => x.TransactionMode, storeId);
                model.LocationId_OverrideForStore = await _settingService.SettingExistsAsync(settings, x => x.LocationId, storeId);
                model.AdditionalFee_OverrideForStore = await _settingService.SettingExistsAsync(settings, x => x.AdditionalFee, storeId);
                model.AdditionalFeePercentage_OverrideForStore = await _settingService.SettingExistsAsync(settings, x => x.AdditionalFeePercentage, storeId);
            }

            //prepare business locations, every payment a merchant processes is associated with one of these locations
            if (!string.IsNullOrEmpty(settings.AccessToken))
            {
                model.Locations = (await _squarePaymentManager.GetActiveLocations(storeId)).Select(location =>
                {
                    var name = location.BusinessName;
                    if (!location.Name.Equals(location.BusinessName))
                        name = $"{name} ({location.Name})";
                    return new SelectListItem { Text = name, Value = location.Id };
                }).ToList();
                if (model.Locations.Any())
                {
                    var selectLocationText = await _localizationService.GetResourceAsync("Plugins.Payments.Square.Fields.Location.Select");
                    model.Locations.Insert(0, new SelectListItem { Text = selectLocationText, Value = "0" });
                }
            }

            //add the special item for 'there are no location' with value 0
            if (!model.Locations.Any())
            {
                var noLocationText = await _localizationService.GetResourceAsync("Plugins.Payments.Square.Fields.Location.NotExist");
                model.Locations.Add(new SelectListItem { Text = noLocationText, Value = "0" });
            }

            //warn admin that the location is a required parameter
            if (string.IsNullOrEmpty(settings.LocationId) || settings.LocationId.Equals("0"))
                _notificationService.WarningNotification(await _localizationService.GetResourceAsync("Plugins.Payments.Square.Fields.Location.Hint"));

            //migrate to using refresh tokens
            if (!settings.UseSandbox && settings.RefreshToken == Guid.Empty.ToString())
            {
                var migrateMessage = $"Your access token is deprecated.<br /> " +
                    $"1. In the <a href=\"http://squ.re/nopcommerce1\" target=\"_blank\">Square Developer Portal</a> make sure your application is on Connect API version 2019-03-13 or later.<br /> " +
                    $"2. On this page click 'Obtain access token' below.<br />";
                _notificationService.ErrorNotification(migrateMessage, encode: false);
            }

            return View("~/Plugins/Payments.Square/Views/Configure.cshtml", model);
        }

        [HttpPost, ActionName("Configure")]
        [FormValueRequired("save")]
        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public async Task<IActionResult> Configure(ConfigurationModel model)
        {
            if (! await _permissionService.AuthorizeAsync(StandardPermissionProvider.ManagePaymentMethods))
                return AccessDeniedView();

            if (!ModelState.IsValid)
                return await Configure();

            //load settings for a chosen store scope
            var storeId = await _storeContext.GetActiveStoreScopeConfigurationAsync();
            var settings = await _settingService.LoadSettingAsync<SquarePaymentSettings>(storeId);

            //save settings
            if (model.UseSandbox)
            {
                settings.ApplicationId = model.SandboxApplicationId;
                settings.ApplicationSecret = string.Empty;
                settings.AccessToken = model.SandboxAccessToken;
            }
            else
            {
                settings.ApplicationId = model.ApplicationId;
                settings.ApplicationSecret = model.ApplicationSecret;
                if (settings.UseSandbox)
                    settings.AccessToken = string.Empty;
            }
            settings.LocationId = model.UseSandbox == settings.UseSandbox ? model.LocationId : string.Empty;
            settings.UseSandbox = model.UseSandbox;
            settings.Use3ds = model.Use3ds;
            settings.TransactionMode = (TransactionMode)model.TransactionModeId;
            settings.AdditionalFee = model.AdditionalFee;
            settings.AdditionalFeePercentage = model.AdditionalFeePercentage;

            await _settingService.SaveSettingAsync(settings, x => x.ApplicationId, storeId, false);
            await _settingService.SaveSettingAsync(settings, x => x.ApplicationSecret, storeId, false);
            await _settingService.SaveSettingAsync(settings, x => x.AccessToken, storeId, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(settings, x => x.UseSandbox, model.UseSandbox_OverrideForStore, storeId, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(settings, x => x.Use3ds, model.Use3ds_OverrideForStore, storeId, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(settings, x => x.TransactionMode, model.TransactionModeId_OverrideForStore, storeId, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(settings, x => x.LocationId, model.LocationId_OverrideForStore, storeId, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(settings, x => x.AdditionalFee, model.AdditionalFee_OverrideForStore, storeId, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(settings, x => x.AdditionalFeePercentage, model.AdditionalFeePercentage_OverrideForStore, storeId, false);

            await _settingService.ClearCacheAsync();

            _notificationService.SuccessNotification(await _localizationService.GetResourceAsync("Admin.Plugins.Saved"));

            return await Configure();
        }

        [HttpPost, ActionName("Configure")]
        [FormValueRequired("obtainAccessToken")]
        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public async Task<IActionResult> ObtainAccessToken(ConfigurationModel model)
        {
            if (! (await _permissionService.AuthorizeAsync(StandardPermissionProvider.ManagePaymentMethods)))
                return AccessDeniedView();

            //load settings for a chosen store scope
            var storeId = await _storeContext.GetActiveStoreScopeConfigurationAsync();
            var settings = await _settingService.LoadSettingAsync<SquarePaymentSettings>(storeId);

            //create new verification string
            settings.AccessTokenVerificationString = Guid.NewGuid().ToString();
            await _settingService.SaveSettingAsync(settings, x => settings.AccessTokenVerificationString, storeId);

            //get the URL to directs a Square merchant's web browser
            var redirectUrl = await _squarePaymentManager.GenerateAuthorizeUrl(storeId);

            return Redirect(redirectUrl);
        }

        public async Task<IActionResult> AccessTokenCallback()
        {
            //load settings for a current store
            var storeId = await _storeContext.GetActiveStoreScopeConfigurationAsync();
            var settings = await _settingService.LoadSettingAsync<SquarePaymentSettings>(storeId);

            //handle access token callback
            try
            {
                if (string.IsNullOrEmpty(settings.ApplicationId) || string.IsNullOrEmpty(settings.ApplicationSecret))
                    throw new NopException("Plugin is not configured");

                //check whether there are errors in the request
                if (Request.Query.TryGetValue("error", out var error) | Request.Query.TryGetValue("error_description", out var errorDescription))
                    throw new NopException($"{error} - {errorDescription}");

                //validate verification string
                if (!Request.Query.TryGetValue("state", out var verificationString) || !verificationString.Equals(settings.AccessTokenVerificationString))
                    throw new NopException("The verification string did not pass the validation");

                //check whether there is an authorization code in the request
                if (!Request.Query.TryGetValue("code", out var authorizationCode))
                    throw new NopException("No service response");

                //exchange the authorization code for an access token
                var (accessToken, refreshToken) = _squarePaymentManager.ObtainAccessToken(authorizationCode, storeId);
                if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
                    throw new NopException("No service response");

                //if access token successfully received, save it for the further usage
                settings.AccessToken = accessToken;
                settings.RefreshToken = refreshToken;

                await _settingService.SaveSettingAsync(settings, x => x.AccessToken, storeId, false);
                await _settingService.SaveSettingAsync(settings, x => x.RefreshToken, storeId, false);

                await _settingService.ClearCacheAsync();

                _notificationService.SuccessNotification(await _localizationService.GetResourceAsync("Plugins.Payments.Square.ObtainAccessToken.Success"));
            }
            catch (Exception exception)
            {
                //display errors
                _notificationService.ErrorNotification(await _localizationService.GetResourceAsync("Plugins.Payments.Square.ObtainAccessToken.Error"));
                if (!string.IsNullOrEmpty(exception.Message))
                    _notificationService.ErrorNotification(exception.Message);
            }

            return RedirectToAction("Configure", "PaymentSquare", new { area = AreaNames.Admin });
        }

        [HttpPost, ActionName("Configure")]
        [FormValueRequired("revokeAccessTokens")]
        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public async Task<IActionResult> RevokeAccessTokens(ConfigurationModel model)
        {
            if (!(await _permissionService.AuthorizeAsync(StandardPermissionProvider.ManagePaymentMethods)))
                return AccessDeniedView();

            //load settings for a chosen store scope
            var storeId = await _storeContext.GetActiveStoreScopeConfigurationAsync();
            var settings = await _settingService.LoadSettingAsync<SquarePaymentSettings>(storeId);

            try
            {
                //try to revoke all access tokens
                var successfullyRevoked = _squarePaymentManager.RevokeAccessTokens(storeId);
                if (!successfullyRevoked)
                    throw new NopException("Tokens were not revoked");

                //if access token successfully revoked, delete it from the settings
                settings.AccessToken = string.Empty;
                await _settingService.SaveSettingAsync(settings, x => x.AccessToken, storeId);

                _notificationService.SuccessNotification(await _localizationService.GetResourceAsync("Plugins.Payments.Square.RevokeAccessTokens.Success"));
            }
            catch (Exception exception)
            {
                var error = await _localizationService.GetResourceAsync("Plugins.Payments.Square.RevokeAccessTokens.Error");
                if (!string.IsNullOrEmpty(exception.Message))
                    error = $"{error} - {exception.Message}";
                _notificationService.ErrorNotification(exception.Message);
            }

            return await Configure();
        }

        #endregion
    }
}