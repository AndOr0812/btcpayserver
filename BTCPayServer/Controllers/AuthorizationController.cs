﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/openiddict-core for more information concerning
 * the license and the contributors participating to this project.
 */

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Authentication.OpenId;
using BTCPayServer.Data;
using BTCPayServer.Models;
using BTCPayServer.Models.Authorization;
using BTCPayServer.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using OpenIddict.Server;
#if NETCOREAPP21
using OpenIddictRequest = AspNet.Security.OpenIdConnect.Primitives.OpenIdConnectRequest;
using OpenIdConnectDefaults = OpenIddict.Server.OpenIddictServerDefaults;
using AspNet.Security.OpenIdConnect.Extensions;
using AspNet.Security.OpenIdConnect.Primitives;
#else
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
#endif

namespace BTCPayServer.Controllers
{
    public class AuthorizationController : Controller
    {
        private readonly OpenIddictApplicationManager<BTCPayOpenIdClient> _applicationManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly OpenIddictAuthorizationManager<BTCPayOpenIdAuthorization> _authorizationManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IOptions<IdentityOptions> _IdentityOptions;

        public AuthorizationController(
            OpenIddictApplicationManager<BTCPayOpenIdClient> applicationManager,
            SignInManager<ApplicationUser> signInManager,
            OpenIddictAuthorizationManager<BTCPayOpenIdAuthorization> authorizationManager,
            UserManager<ApplicationUser> userManager,
            IOptions<IdentityOptions> identityOptions)
        {
            _applicationManager = applicationManager;
            _signInManager = signInManager;
            _authorizationManager = authorizationManager;
            _userManager = userManager;
            _IdentityOptions = identityOptions;
        }

        [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)] 
        [HttpGet("/connect/authorize")]
        public async Task<IActionResult> Authorize(OpenIddictRequest openIdConnectRequest)
        {
            // Retrieve the application details from the database.
            var application = await _applicationManager.FindByClientIdAsync(openIdConnectRequest.ClientId);

            if (application == null)
            {
                return View("Error",
                    new ErrorViewModel
                    {
                        Error = OpenIddictConstants.Errors.InvalidClient,
                        ErrorDescription =
                            "Details concerning the calling client application cannot be found in the database"
                    });
            }

            var userId = _userManager.GetUserId(User);
            if (!string.IsNullOrEmpty(
                await OpenIdExtensions.IsUserAuthorized(_authorizationManager, openIdConnectRequest, userId, application.Id)))
            {
                return await Authorize(openIdConnectRequest, "YES", false);
            }

            // Flow the request_id to allow OpenIddict to restore
            // the original authorization request from the cache.
            return View(new AuthorizeViewModel
            {
                ApplicationName = await _applicationManager.GetDisplayNameAsync(application),
                RequestId = openIdConnectRequest.RequestId,
                Scope = openIdConnectRequest.GetScopes()
            });
        }

        [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [HttpPost("/connect/authorize")]
        public async Task<IActionResult> Authorize(OpenIddictRequest openIdConnectRequest,
            string consent, bool createAuthorization = true)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return View("Error",
                    new ErrorViewModel
                    {
                        Error = OpenIddictConstants.Errors.ServerError,
                        ErrorDescription = "The specified user could not be found"
                    });
            }

            string type = null;
            switch (consent.ToUpperInvariant())
            {
                case "YESTEMPORARY":
                    type = OpenIddictConstants.AuthorizationTypes.AdHoc;
                    break;
                case "YES":
                    type = OpenIddictConstants.AuthorizationTypes.Permanent;
                    break;
                case "NO":
                default:
                    // Notify OpenIddict that the authorization grant has been denied by the resource owner
                    // to redirect the user agent to the client application using the appropriate response_mode.
                    return Forbid(OpenIdConnectDefaults.AuthenticationScheme);
            }


            // Create a new authentication ticket.
            var ticket =
                await OpenIdExtensions.CreateAuthenticationTicket(_applicationManager, _authorizationManager,
                    _IdentityOptions.Value, _signInManager,
                    openIdConnectRequest, user);
            if (createAuthorization)
            {
                var application = await _applicationManager.FindByClientIdAsync(openIdConnectRequest.ClientId);
                var authorization = await _authorizationManager.CreateAsync(User, user.Id, application.Id,
                    type, ticket.GetScopes().ToImmutableArray(),
                    ticket.Properties.Items.ToImmutableDictionary());
                ticket.SetInternalAuthorizationId(authorization.Id);
            }

            // Returning a SignInResult will ask OpenIddict to issue the appropriate access/identity tokens.
            return SignIn(ticket.Principal, ticket.Properties, ticket.AuthenticationScheme);
        }
    }
}
