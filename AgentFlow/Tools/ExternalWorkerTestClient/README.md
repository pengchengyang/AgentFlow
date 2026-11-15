# ExternalWorkerTestClient

Command-line FakeCore harness used to validate external Worker handshake, lifecycle and Pin traffic.

The bundled external demos expose two inputs and two outputs:

```text
input_string_toupper -> output_string_toupper
input_string_tolower -> output_string_tolower
```

The harness verifies lifecycle, both Pin routes and broadcast bridging. Use
`--lower-input` to send the second input instead of the default uppercase route.
