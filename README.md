Forked from [TcHaxx/TcPnScanner](https://github.com/TcHaxx/TcPnScanner)

Enhanced by the option:

`--device-file` Path to a json dictionary file with Vendor- and Device IDs.

#### Example content of a device file
```
{
    "pn-device-1": "0x002a0313",
    "pn-device-2": "0x002a0314"
}
```
Where the key represents the expected profinet name and
the value represents Vendor- and Device ID as hexadecimal string.
