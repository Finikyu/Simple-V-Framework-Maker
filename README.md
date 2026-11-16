# Simple V Framework Maker

The app ports MGSV: TPP replacer mods to V Framework 1.7 content. It ports outfits, weapons, thrown items and buddy costumes. It also edits existing V Framework mods. It makes one .mgsv file that installs with SnakeBite and Diamond Mod Manager.

Version 0.2.0 is a pre-release.

## What you need

- Metal Gear Solid V: The Phantom Pain on PC.
- SnakeBite or Diamond Mod Manager.
- To play the mods that you make: V Framework 1.7, Infinite Heaven and IHHook.

V Framework: https://github.com/Yazed0071/V_Framework
V Framework guide: https://mgsvmoddingwiki.github.io/V_Framework/

## Start the app

1. Extract the zip file.
2. Keep the SimpleVFrameworkMaker folder in a fixed place, for example your Documents folder. Do not start the app from the zip or from the Downloads folder.
3. Double-click SimpleVFrameworkMaker.exe.
4. Windows can show "Windows protected your PC", because the exe has no signature. If you trust this download, click "More info". Then click "Run anyway".
5. On the first start, the app asks for your game folder. This is the folder that holds the "master" folder.

The app keeps its settings and its logs in the "settings" folder next to the exe.

## Make a mod

1. Click "Choose a file". Open a .mgsv or .zip mod.
2. Click "Continue to setup".
3. Edit the items: names, descriptions, icons, abilities, camo values and R&D values.
4. Click "Review & build". Then click "Build".
5. Install the new .mgsv file with SnakeBite or Diamond Mod Manager.

To keep your edits, click Save. The app writes a project file (.svfm). Open it later to continue.

## Report a problem

1. Click Help > Save problem report. You can also click "Save problem report" on the red error banner.
2. The app saves the file "Problem report <date> <time>.txt" next to the exe. Explorer shows the file. The app opens this GitHub form:
   https://github.com/Finikyu/Simple-V-Framework-Maker/issues/new?template=problem.yml
3. Attach the file to the form. Tell what you did and what went wrong.

If the app stops at start, it writes the problem report next to the exe. Send it with the same form.

The app sends nothing on its own. The report shows your user folder as %USERPROFILE%.

## Send feedback

Click Help > Send feedback, or open this form:
https://github.com/Finikyu/Simple-V-Framework-Maker/issues/new?template=feedback.yml

Ideas, questions and mods that do not port well are all welcome.

All issues: https://github.com/Finikyu/Simple-V-Framework-Maker/issues

## Credits

- Simple V Framework Maker by Finikyu.
- V Framework by Yazed0071: https://github.com/Yazed0071/V_Framework
- Archive format: ported from GzsTool 0.5.3 by Atvaark: https://github.com/Atvaark/GzsTool
- Package rules: the same as SnakeBite 0.9.x by topher-au and TinManTex.
- QAR path dictionary: kapuragu/mgsv-lookup-strings.
- Thanks to the authors of the mods that the app was tested with.

## License

The app is under the Unlicense (see LICENSE). The parts in "Third-party notices" keep their own licenses.
