using HcmIdentityProvisioning.Admin;
using HcmIdentityProvisioning.Admin.Descriptors;

var app = HcmAdminCliBuilder.Create(args)
    .AddConnector<SyntheticHcmConnectorCliDescriptor>()
    .AddConnector<GenericRestHcmConnectorCliDescriptor>();

return await app.RunAsync();
