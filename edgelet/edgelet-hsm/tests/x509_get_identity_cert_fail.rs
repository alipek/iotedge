// Copyright (c) Microsoft. All rights reserved.

#![deny(unused_extern_crates, warnings)]
#![deny(clippy::all, clippy::pedantic)]

use edgelet_core::GetDeviceIdentityCertificate;
use edgelet_hsm::X509;
use lazy_static::lazy_static;
use std::sync::Mutex;
mod test_utils;
use test_utils::TestHSMEnvSetup;

lazy_static! {
    static ref LOCK: Mutex<()> = Mutex::new(());
}

#[test]
fn x509_get_identity_cert_fails() {
    // arrange
    let _setup_home_dir = TestHSMEnvSetup::new(&LOCK, None);

    let x509 = X509::new().unwrap();

    let cert_info = x509.get();

    assert!(cert_info.is_err());
}
