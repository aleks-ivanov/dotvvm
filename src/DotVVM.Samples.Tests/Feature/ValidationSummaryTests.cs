﻿using DotVVM.Samples.Tests.Base;
using DotVVM.Testing.Abstractions;
using Riganti.Selenium.Core;
using Xunit;

namespace DotVVM.Samples.Tests.Feature
{
    public class ValidationSummaryTests : AppSeleniumTest
    {
        public ValidationSummaryTests(Xunit.Abstractions.ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void Control_ValidationSummary_RecursiveValidationSummary()
        {
            RunInAllBrowsers(browser => {
                browser.NavigateToUrl(SamplesRouteUrls.ControlSamples_ValidationSummary_RecursiveValidationSummary);

                browser.ElementAt("input[type=button]", 0).Click().Wait();

                browser.ElementAt("ul", 0).FindElements("li").ThrowIfDifferentCountThan(2);
                AssertUI.InnerTextEquals(browser.First("#result"), "false");

                browser.ElementAt("input[type=button]", 1).Click().Wait();
                browser.ElementAt("ul", 1).FindElements("li").ThrowIfDifferentCountThan(1);
                AssertUI.InnerTextEquals(browser.First("#result"), "false");
            });
        }

        [Theory]
        [InlineData(SamplesRouteUrls.ControlSamples_ValidationSummary_IncludeErrorsFromTarget_PropertyPathNull)]
        [InlineData(SamplesRouteUrls.ControlSamples_ValidationSummary_IncludeErrorsFromTarget_PropertyPathNotNull)]
        [SampleReference(nameof(SamplesRouteUrls.ControlSamples_ValidationSummary_IncludeErrorsFromTarget_PropertyPathNull))]
        [SampleReference(nameof(SamplesRouteUrls.ControlSamples_ValidationSummary_IncludeErrorsFromTarget_PropertyPathNotNull))]
        public void Control_ValidationSummary_IncludeErrorsFromTarget(string url)
        {
            RunInAllBrowsers(browser => {
                browser.NavigateToUrl(url);

                var summary = browser.First("[data-ui=validationSummary]");
                Assert.Equal(0, summary.Children.Count);

                var loginButton = browser.First("[data-ui=login-button]");
                loginButton.Click();
                Assert.Equal(2, summary.Children.Count);

                browser.First("[data-ui=nick-textbox]").SendKeys("Mike");
                loginButton.Click();
                Assert.Equal(1, summary.Children.Count);

                browser.First("[data-ui=password-textbox]").SendKeys("123");
                loginButton.Click();
                Assert.Equal(1, summary.Children.Count);

                browser.First("[data-ui=password-textbox]").SendKeys("4");
                loginButton.Click().Wait();
                Assert.Equal(0, summary.Children.Count);
            });
        }
    }
}
