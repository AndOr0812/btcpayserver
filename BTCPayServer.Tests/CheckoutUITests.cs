using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Tests.Logging;
using BTCPayServer.Views.Stores;
using NBitpayClient;
using OpenQA.Selenium;
using OpenQA.Selenium.Interactions;
using OpenQA.Selenium.Support.UI;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Tests
{
    [Trait("Selenium", "Selenium")]
    public class CheckoutUITests
    {
        public const int TestTimeout = 60_000;
        public CheckoutUITests(ITestOutputHelper helper)
        {
            Logs.Tester = new XUnitLog(helper) {Name = "Tests"};
            Logs.LogProvider = new XUnitLogProvider(helper);
        }

        [Fact(Timeout = TestTimeout)]
        public async Task CanHandleRefundEmailForm()
        {

            using (var s = SeleniumTester.Create())
            {
                await s.StartAsync();
                s.RegisterNewUser();
                var store = s.CreateNewStore();
                s.AddDerivationScheme("BTC");

                var emailAlreadyThereInvoiceId =s.CreateInvoice(store.storeName, 100, "USD", "a@g.com");
                s.GoToInvoiceCheckout(emailAlreadyThereInvoiceId);
                s.Driver.AssertElementNotFound(By.Id("emailAddressFormInput"));
                s.GoToHome();
                var invoiceId = s.CreateInvoice(store.storeName);
                s.Driver.FindElement(By.ClassName("invoice-details-link")).Click();
                s.Driver.AssertNoError();
                s.Driver.Navigate().Back();
                s.Driver.FindElement(By.ClassName("invoice-checkout-link")).Click();
                Assert.NotEmpty(s.Driver.FindElements(By.Id("checkoutCtrl")));

                Assert.True(s.Driver.FindElement(By.Id("emailAddressFormInput")).Displayed);
                s.Driver.FindElement(By.Id("emailAddressFormInput")).SendKeys("xxx");
                s.Driver.FindElement(By.Id("emailAddressForm")).FindElement(By.CssSelector("button.action-button"))
                    .Click();
                var formInput = s.Driver.FindElement(By.Id("emailAddressFormInput"));
                
                Assert.True(formInput.Displayed);
                Assert.Contains("form-input-invalid", formInput.GetAttribute("class"));
                formInput = s.Driver.FindElement(By.Id("emailAddressFormInput"));
                formInput.SendKeys("@g.com");
                var actionButton = s.Driver.FindElement(By.Id("emailAddressForm")).FindElement(By.CssSelector("button.action-button"));
                actionButton.Click();

                s.Driver.AssertElementNotFound(By.Id("emailAddressFormInput"));                
                s.Driver.Navigate().Refresh();
                s.Driver.AssertElementNotFound(By.Id("emailAddressFormInput"));
            }
        }

        [Fact(Timeout = TestTimeout)]
        public async Task CanUseLanguageDropdown()
        {
            using (var s = SeleniumTester.Create())
            {
                await s.StartAsync();
                s.RegisterNewUser();
                var store = s.CreateNewStore();
                s.AddDerivationScheme("BTC");

                var invoiceId = s.CreateInvoice(store.storeName);
                s.GoToInvoiceCheckout(invoiceId);
                Assert.True(s.Driver.FindElement(By.Id("DefaultLang")).FindElements(By.TagName("option")).Count > 1);
                var payWithTextEnglish = s.Driver.FindElement(By.Id("pay-with-text")).Text;
                
                var prettyDropdown = s.Driver.FindElement(By.Id("prettydropdown-DefaultLang"));
                prettyDropdown.Click();
                await Task.Delay(200);
                prettyDropdown.FindElement(By.CssSelector("[data-value=\"da-DK\"]")).Click();
                await Task.Delay(1000);
                Assert.NotEqual(payWithTextEnglish, s.Driver.FindElement(By.Id("pay-with-text")).Text);
                s.Driver.Navigate().GoToUrl(s.Driver.Url + "?lang=da-DK");
                
                Assert.NotEqual(payWithTextEnglish, s.Driver.FindElement(By.Id("pay-with-text")).Text);
                
                s.Driver.Quit();
            }
        }
        
        [Fact(Timeout = TestTimeout)]
        public async Task CanUsePaymentMethodDropdown()
        {
            using (var s = SeleniumTester.Create())
            {
                await s.StartAsync();
                s.RegisterNewUser();
                var store = s.CreateNewStore();
                s.AddDerivationScheme("BTC");
                
                //check that there is no dropdown since only one payment method is set
                var invoiceId = s.CreateInvoice(store.storeName, 10, "USD", "a@g.com");
                s.GoToInvoiceCheckout(invoiceId);
                s.Driver.FindElement(By.ClassName("payment__currencies_noborder"));
                s.GoToHome();
                s.GoToStore(store.storeId);
                s.AddDerivationScheme("LTC");
                s.AddLightningNode("BTC",LightningConnectionType.CLightning);
                //there should be three now
                invoiceId = s.CreateInvoice(store.storeName, 10, "USD", "a@g.com");
                s.GoToInvoiceCheckout(invoiceId);
                var currencyDropdownButton =  s.Driver.FindElement(By.ClassName("payment__currencies"));
                Assert.Contains("BTC", currencyDropdownButton.Text);
                currencyDropdownButton.Click();
                
                var elements = s.Driver.FindElement(By.ClassName("vex-content")).FindElements(By.ClassName("vexmenuitem"));
                Assert.Equal(3, elements.Count);
                elements.Single(element => element.Text.Contains("LTC")).Click();
                Thread.Sleep(1000);
                currencyDropdownButton =  s.Driver.FindElement(By.ClassName("payment__currencies"));
                Assert.Contains("LTC", currencyDropdownButton.Text);
                currencyDropdownButton.Click();

                elements = s.Driver.FindElement(By.ClassName("vex-content")).FindElements(By.ClassName("vexmenuitem"));
                elements.Single(element => element.Text.Contains("Lightning")).Click();
                currencyDropdownButton = s.Driver.FindElement(By.ClassName("payment__currencies"));

                Assert.Contains("Lightning", currencyDropdownButton.Text);
                
                s.Driver.Quit();
            }
        }
        
        [Fact(Timeout = TestTimeout)]
        public async Task CanUseLightningSatsFeature()
        {
            using (var s = SeleniumTester.Create())
            {
                await s.StartAsync();
                s.RegisterNewUser();
                var store = s.CreateNewStore();
                s.AddInternalLightningNode("BTC");
                s.GoToStore(store.storeId, StoreNavPages.Checkout);
                s.SetCheckbox(s, "LightningAmountInSatoshi", true);
                var command = s.Driver.FindElement(By.Name("command"));
               
                command.ForceClick();
                var invoiceId = s.CreateInvoice(store.storeName, 10, "USD", "a@g.com");
                s.GoToInvoiceCheckout(invoiceId);
                Assert.Contains("Sats", s.Driver.FindElement(By.ClassName("payment__currencies_noborder")).Text);
                
            }
        }
    }
}
