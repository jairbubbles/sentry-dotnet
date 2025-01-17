﻿using System.IO;
using System.Web;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Sentry.AspNet.Tests
{
    public class HttpContextExtensionsTests
    {
        [Fact]
        public void StartSentryTransaction_CreatesValidTransaction()
        {
            // Arrange
            var context = new HttpContext(
                new HttpRequest("foo", "https://localhost/person/13", "details=true")
                {
                    RequestType = "GET"
                },
                new HttpResponse(TextWriter.Null)
            );

            // Act
            var transaction = context.StartSentryTransaction();

            // Assert
            transaction.Name.Should().Be("GET /person/13");
            transaction.Operation.Should().Be("http.server");
        }

        [Fact]
        public void StartSentryTransaction_BindsToScope()
        {
            // Arrange
            using var _ = SentrySdk.UseHub(new Sentry.Internal.Hub(
                Substitute.For<ISentryClient>(),
                new SentryOptions {Dsn = "https://d4d82fc1c2c4032a83f3a29aa3a3aff@fake-sentry.io:65535/2147483647"}
            ));

            var context = new HttpContext(
                new HttpRequest("foo", "https://localhost/person/13", "details=true")
                {
                    RequestType = "GET"
                },
                new HttpResponse(TextWriter.Null)
            );

            // Act
            var transaction = context.StartSentryTransaction();
            var transactionFromScope = SentrySdk.GetSpan();

            // Assert
            transactionFromScope.Should().BeSameAs(transaction);
        }
    }
}
