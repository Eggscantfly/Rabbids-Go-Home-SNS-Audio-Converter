# Rabbids Go Home SNS Audio Converter

This tool converts standard 16-bit WAV files into Ubisoft's LyN **.sns** format.

## Requirements

- Microsoft .NET 8    
- oggenc2 and FFmpeg in the app's root directory (or available in the system path)    
- Windows PC  

## Other info

This tool could possibly be used for other LyN Engine Titles e.g. (Rabbids Travel in Time, Rabbids Land, Just Dance 2, Zombie U ect. )

## Downsides
Sadly there's no way to generate a sns file that has the Label string with the beat markers in them yet...  
If you want to replace a sns i.e. (**A Stereo sns with the Sample Rate of 32000hz**) Your custom audio has to have the same Sample Rate and the same amount of Channels as the sns you are trying to replace.

## Credits

Shoutout to vgmstream for Reverse Engineering this file format and figuring out the DSP coefficients and other things! Without them this tool wouldnt exist.

https://github.com/vgmstream/vgmstream
