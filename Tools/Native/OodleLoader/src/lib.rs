//! Local-only ABI adapter for a user-supplied runtime. No search, copying or downloading.
use std::{ffi::OsString, path::PathBuf, sync::OnceLock};

#[derive(Debug, Clone, Copy)]
#[repr(i32)]
pub enum Compressor { None = 3, Kraken = 8, Leviathan = 13, Mermaid = 9, Selkie = 11, Hydra = 12 }

#[derive(Debug, Clone, Copy)]
#[repr(i32)]
pub enum CompressionLevel {
    None = 0, SuperFast = 1, VeryFast = 2, Fast = 3, Normal = 4,
    Optimal1 = 5, Optimal2 = 6, Optimal3 = 7, Optimal4 = 8, Optimal5 = 9,
    HyperFast1 = -1, HyperFast2 = -2, HyperFast3 = -3, HyperFast4 = -4,
}

#[derive(thiserror::Error, Debug)]
pub enum Error {
    #[error("Oodle requires BATCOMPUTER_OODLE_DLL to name an existing absolute path to your local runtime. Configure Oodle in Batcomputer Settings. No runtime will be downloaded.")]
    MissingRuntime,
    #[error("Cannot load the configured local Oodle runtime: {0}")]
    Initialization(String),
    #[error("Oodle compression failed")]
    CompressionFailed,
}

type Compress = unsafe extern "system" fn(Compressor, *const u8, usize, *mut u8, CompressionLevel, *const (), *const (), *const (), *mut (), usize) -> isize;
type Decompress = unsafe extern "system" fn(*const u8, usize, *mut u8, usize, u32, u32, u32, u64, u64, u64, u64, *mut u8, usize, u32) -> isize;
type BufferSize = unsafe extern "system" fn(Compressor, usize) -> usize;
type SetPrintf = unsafe extern "system" fn(*const ());

pub struct Oodle {
    _library: libloading::Library,
    compress: Compress,
    decompress: Decompress,
    buffer_size: BufferSize,
}

fn runtime_path(value: Option<OsString>) -> Result<PathBuf, Error> {
    let path = PathBuf::from(value.ok_or(Error::MissingRuntime)?);
    if !path.is_absolute() || !path.is_file() { return Err(Error::MissingRuntime); }
    Ok(path)
}

impl Oodle {
    fn load() -> Result<Self, Error> {
        let path = runtime_path(std::env::var_os("BATCOMPUTER_OODLE_DLL"))?;
        // The user explicitly selected this local DLL; never fall back to PATH or a web host.
        unsafe {
            let lib = libloading::Library::new(path).map_err(|e| Error::Initialization(e.to_string()))?;
            let compress = *lib.get::<Compress>(b"OodleLZ_Compress").map_err(|e| Error::Initialization(e.to_string()))?;
            let decompress = *lib.get::<Decompress>(b"OodleLZ_Decompress").map_err(|e| Error::Initialization(e.to_string()))?;
            let buffer_size = *lib.get::<BufferSize>(b"OodleLZ_GetCompressedBufferSizeNeeded").map_err(|e| Error::Initialization(e.to_string()))?;
            if let Ok(set_printf) = lib.get::<SetPrintf>(b"OodleCore_Plugins_SetPrintf") { set_printf(std::ptr::null()); }
            Ok(Self { _library: lib, compress, decompress, buffer_size })
        }
    }

    pub fn compress(&self, input: &[u8], compressor: Compressor, level: CompressionLevel) -> Result<Vec<u8>, Error> {
        let size = unsafe { (self.buffer_size)(compressor, input.len()) };
        if size < input.len() || size > isize::MAX as usize { return Err(Error::CompressionFailed); }
        let mut output = vec![0; size];
        let written = unsafe { (self.compress)(compressor, input.as_ptr(), input.len(), output.as_mut_ptr(), level,
            std::ptr::null(), std::ptr::null(), std::ptr::null(), std::ptr::null_mut(), 0) };
        if written <= 0 || written as usize > output.len() { return Err(Error::CompressionFailed); }
        output.truncate(written as usize);
        Ok(output)
    }

    pub fn decompress(&self, input: &[u8], output: &mut [u8]) -> isize {
        unsafe { (self.decompress)(input.as_ptr(), input.len(), output.as_mut_ptr(), output.len(),
            1, 1, 0, 0, 0, 0, 0, std::ptr::null_mut(), 0, 3) }
    }
}

static OODLE: OnceLock<Result<Oodle, String>> = OnceLock::new();
pub fn oodle() -> Result<&'static Oodle, Error> {
    OODLE.get_or_init(|| Oodle::load().map_err(|e| e.to_string())).as_ref()
        .map_err(|e| Error::Initialization(e.clone()))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test] fn no_implicit_runtime() { assert!(runtime_path(None).is_err()); }
    #[test] fn relative_runtime_rejected() { assert!(runtime_path(Some("oo2core_9_win64.dll".into())).is_err()); }
    #[test] fn missing_absolute_runtime_rejected() {
        assert!(runtime_path(Some(std::env::temp_dir().join("batcomputer-no-such-oodle.dll").into())).is_err());
    }
}
