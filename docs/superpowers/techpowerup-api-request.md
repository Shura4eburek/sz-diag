Subject: API access request — GPU subsystem ID lookup for internal diagnostic tooling

Hello TechPowerUp team,

We run an internal PC service-center diagnostic tool that inspects client machines
remotely (CPU, RAM, GPU, storage, etc.) as part of hardware troubleshooting. For GPUs,
standard OS/driver APIs (Windows WMI, NVIDIA's own tools) only ever report the reference
chip name (e.g. "NVIDIA GeForce RTX 5060 Ti") — they never expose which board partner
manufactured the card or which retail SKU it is (e.g. MSI Gaming vs. Ventus vs. Shadow,
8GB vs. 16GB variant).

The PCI subsystem vendor/device ID pair (e.g. subvendor 1462 / subdevice 5362) does
identify the exact board partner and SKU, and your GPU database already maps these
combinations to specific card models. We'd like to query that mapping programmatically
as part of our diagnostic pipeline, instead of manually cross-referencing your website
for every machine we check.

Could you tell us:
- Whether a public or partner API exists for looking up GPU subsystem IDs
  (subvendor/subdevice) against your database, and how to get access to it.
- Expected request format and response format.
- Any rate limits, authentication requirements, or usage terms (this would be low-volume,
  internal tooling use — not a bulk scraping or commercial resale use case).
- Whether there's a cost associated with this access.

Happy to provide more detail about our use case if useful. Thanks for maintaining such
a thorough hardware database — it's been genuinely useful for manual lookups already.

Best regards,
[Your name]
