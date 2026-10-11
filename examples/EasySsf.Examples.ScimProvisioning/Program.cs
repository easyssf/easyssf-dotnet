using EasySsf.Examples.ScimProvisioning;
using EasySsf.Testing;

// Keycloak does not emit SCIM Events, so the example brings its own transmitter: the in-process
// TestTransmitter of EasySsf.Testing, which the demo SCIM service provider feeds with the life of
// two users. It is started before the application, because the receiver needs the issuer of its
// transmitter in the configuration. Against a real SCIM service provider, build the application
// without a transmitter and put its issuer, token and delivery method into appsettings.json.
using TestTransmitter transmitter = new();
WebApplication app = ScimProvisioningApplication.Build(args, transmitter);
app.Run();
