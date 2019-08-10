﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using XUnity.AutoTranslator.Plugin.Core.Configuration;
using XUnity.AutoTranslator.Plugin.Core.Extensions;
using XUnity.AutoTranslator.Plugin.Core.Parsing;
using XUnity.AutoTranslator.Plugin.Core.Utilities;
using XUnity.AutoTranslator.Plugin.Utilities;

namespace XUnity.AutoTranslator.Plugin.Core
{
   class TextTranslationCache
   {
      private static readonly char[] TranslationSplitters = new char[] { '=' };

      /// <summary>
      /// All the translations are stored in this dictionary.
      /// </summary>
      private Dictionary<string, string> _staticTranslations = new Dictionary<string, string>();
      private Dictionary<string, string> _translations = new Dictionary<string, string>();
      private Dictionary<string, string> _reverseTranslations = new Dictionary<string, string>();
      private Dictionary<string, string> _tokenTranslations = new Dictionary<string, string>();
      private Dictionary<string, string> _reverseTokenTranslations = new Dictionary<string, string>();
      private HashSet<string> _partialTranslations = new HashSet<string>();

      private Dictionary<int, TranslationDictionaries> _scopedTranslations = new Dictionary<int, TranslationDictionaries>();
      //private Dictionary<string, TranslationDictionaries> _scopedTokenTranslations = new Dictionary<string, TranslationDictionaries>();

      private List<RegexTranslation> _defaultRegexes = new List<RegexTranslation>();
      private HashSet<string> _registeredRegexes = new HashSet<string>();

      /// <summary>
      /// These are the new translations that has not yet been persisted to the file system.
      /// </summary>
      private object _writeToFileSync = new object();
      private Dictionary<string, string> _newTranslations = new Dictionary<string, string>();

      public TextTranslationCache()
      {
         LoadStaticTranslations();
      }

      private static IEnumerable<string> GetTranslationFiles()
      {
         return Directory.GetFiles( Path.Combine( PluginEnvironment.Current.TranslationPath, Settings.TranslationDirectory ).Parameterize(), $"*.txt", SearchOption.AllDirectories )
            .Select( x => x.Replace( "/", "\\" ) );
      }

      internal void LoadTranslationFiles()
      {
         try
         {
            var startTime = Time.realtimeSinceStartup;
            lock( _writeToFileSync )
            {
               Directory.CreateDirectory( Path.Combine( PluginEnvironment.Current.TranslationPath, Settings.TranslationDirectory ).Parameterize() );
               Directory.CreateDirectory( Path.GetDirectoryName( Settings.AutoTranslationsFilePath ) );

               _registeredRegexes.Clear();
               _defaultRegexes.Clear();
               _translations.Clear();
               _reverseTranslations.Clear();
               _partialTranslations.Clear();
               _tokenTranslations.Clear();
               _reverseTokenTranslations.Clear();
               _scopedTranslations.Clear();
               Settings.Replacements.Clear();

               var mainTranslationFile = Settings.AutoTranslationsFilePath;
               var substitutionFile = Settings.SubstitutionFilePath;
               LoadTranslationsInFile( mainTranslationFile, false, true );
               LoadTranslationsInFile( substitutionFile, true, false );
               foreach( var fullFileName in GetTranslationFiles().Reverse().Except( new[] { mainTranslationFile, substitutionFile } ) )
               {
                  LoadTranslationsInFile( fullFileName, false, false );
               }
            }
            var endTime = Time.realtimeSinceStartup;
            XuaLogger.Current.Info( $"Loaded translation text files (took {Math.Round( endTime - startTime, 2 )} seconds)" );

            // generate variations of created translations
            {
               startTime = Time.realtimeSinceStartup;

               foreach( var kvp in _translations.ToList() )
               {
                  // also add a modified version of the translation
                  var ukey = new UntranslatedText( kvp.Key, false, true, Settings.FromLanguageUsesWhitespaceBetweenWords );
                  var uvalue = new UntranslatedText( kvp.Value, false, true, Settings.ToLanguageUsesWhitespaceBetweenWords );
                  if( ukey.Original_Text_ExternallyTrimmed != kvp.Key && !HasTranslated( ukey.Original_Text_ExternallyTrimmed, TranslationScopes.None, false ) )
                  {
                     AddTranslation( ukey.Original_Text_ExternallyTrimmed, uvalue.Original_Text_ExternallyTrimmed, TranslationScopes.None );
                  }
                  if( ukey.Original_Text_ExternallyTrimmed != ukey.Original_Text_FullyTrimmed && !HasTranslated( ukey.Original_Text_FullyTrimmed, TranslationScopes.None, false ) )
                  {
                     AddTranslation( ukey.Original_Text_FullyTrimmed, uvalue.Original_Text_FullyTrimmed, TranslationScopes.None );
                  }
               }

               // Generate variations for scoped
               foreach( var scopeKvp in _scopedTranslations.ToList() )
               {
                  var scope = scopeKvp.Key;

                  foreach( var kvp in scopeKvp.Value.Translations.ToList() )
                  {
                     // also add a modified version of the translation
                     var ukey = new UntranslatedText( kvp.Key, false, true, Settings.FromLanguageUsesWhitespaceBetweenWords );
                     var uvalue = new UntranslatedText( kvp.Value, false, true, Settings.ToLanguageUsesWhitespaceBetweenWords );
                     if( ukey.Original_Text_ExternallyTrimmed != kvp.Key && !HasTranslated( ukey.Original_Text_ExternallyTrimmed, scope, false ) )
                     {
                        AddTranslation( ukey.Original_Text_ExternallyTrimmed, uvalue.Original_Text_ExternallyTrimmed, scope );
                     }
                     if( ukey.Original_Text_ExternallyTrimmed != ukey.Original_Text_FullyTrimmed && !HasTranslated( ukey.Original_Text_FullyTrimmed, scope, false ) )
                     {
                        AddTranslation( ukey.Original_Text_FullyTrimmed, uvalue.Original_Text_FullyTrimmed, scope );
                     }
                  }
               }

               XuaLogger.Current.Info( $"Created variation translations (took {Math.Round( endTime - startTime, 2 )} seconds)" );
               endTime = Time.realtimeSinceStartup;
            }

            // generate token translations, which are online allowed when getting 'token' translations
            if( Settings.GeneratePartialTranslations )
            {
               startTime = Time.realtimeSinceStartup;

               foreach( var kvp in _translations.ToList() )
               {
                  CreatePartialTranslationsFor( kvp.Key, kvp.Value );
               }


               XuaLogger.Current.Info( $"Created partial translations (took {Math.Round( endTime - startTime, 2 )} seconds)" );
               endTime = Time.realtimeSinceStartup;
            }

            var parser = new RichTextParser();
            startTime = Time.realtimeSinceStartup;

            foreach( var kvp in _translations.ToList() )
            {
               var untranslatedResult = parser.Parse( kvp.Key, TranslationScopes.None );
               if( untranslatedResult.Succeeded )
               {
                  var translatedResult = parser.Parse( kvp.Value, TranslationScopes.None );
                  if( translatedResult.Succeeded && untranslatedResult.Arguments.Count == untranslatedResult.Arguments.Count )
                  {
                     foreach( var ukvp in untranslatedResult.Arguments )
                     {
                        var untranslatedToken = ukvp.Value;
                        if( translatedResult.Arguments.TryGetValue( ukvp.Key, out var translatedToken ) )
                        {
                           AddTokenTranslation( untranslatedToken, translatedToken, TranslationScopes.None );
                        }
                     }
                  }
               }
            }

            foreach( var scopedKvp in _scopedTranslations.ToList() )
            {
               var scope = scopedKvp.Key;

               foreach( var kvp in scopedKvp.Value.Translations.ToList() )
               {
                  var untranslatedResult = parser.Parse( kvp.Key, scope );
                  if( untranslatedResult.Succeeded )
                  {
                     var translatedResult = parser.Parse( kvp.Value, scope );
                     if( translatedResult.Succeeded && untranslatedResult.Arguments.Count == untranslatedResult.Arguments.Count )
                     {
                        foreach( var ukvp in untranslatedResult.Arguments )
                        {
                           var untranslatedToken = ukvp.Value;
                           if( translatedResult.Arguments.TryGetValue( ukvp.Key, out var translatedToken ) )
                           {
                              AddTokenTranslation( untranslatedToken, translatedToken, scope );
                           }
                        }
                     }
                  }
               }
            }

            XuaLogger.Current.Info( $"Created token translations (took {Math.Round( endTime - startTime, 2 )} seconds)" );
            endTime = Time.realtimeSinceStartup;

            XuaLogger.Current.Info( $"Global translations generated: {_translations.Count}" );
            XuaLogger.Current.Info( $"Global regex translations generated: {_defaultRegexes.Count}" );
            XuaLogger.Current.Info( $"Global token translations generated: {_tokenTranslations.Count}" );
            if( Settings.GeneratePartialTranslations )
            {
               XuaLogger.Current.Info( $"Global partial translations generated: {_partialTranslations.Count}" );
            }
            foreach( var kvp in _scopedTranslations.OrderBy( x => x.Key ) )
            {
               var scope = kvp.Key;
               var dicts = kvp.Value;

               XuaLogger.Current.Info( $"Scene {scope} translations generated: {dicts.Translations.Count}" );
               XuaLogger.Current.Info( $"Scene {scope} regex translations generated: {dicts.DefaultRegexes.Count}" );
               XuaLogger.Current.Info( $"Scene {scope} token translations generated: {dicts.TokenTranslations.Count}" );
            }
         }
         catch( Exception e )
         {
            XuaLogger.Current.Error( e, "An error occurred while loading translations." );
         }
      }

      private void CreatePartialTranslationsFor( string originalText, string translatedText )
      {
         // determine how many tokens in both strings
         var originalTextTokens = Tokenify( originalText );
         var translatedTextTokens = Tokenify( translatedText );

         double rate = translatedTextTokens.Count / (double)originalTextTokens.Count;

         int translatedTextCursor = 0;
         string originalTextPartial = string.Empty;
         string translatedTextPartial = string.Empty;

         for( int i = 0; i < originalTextTokens.Count; i++ )
         {
            var originalTextToken = originalTextTokens[ i ];
            if( originalTextToken.IsVariable )
            {
               originalTextPartial += "{{" + originalTextToken.Character + "}}";
            }
            else
            {
               originalTextPartial += originalTextToken.Character;
            }

            var length = (int)( Math.Round( ( i + 1 ) * rate, 0, MidpointRounding.AwayFromZero ) );
            for( int j = translatedTextCursor; j < length; j++ )
            {
               var translatedTextToken = translatedTextTokens[ j ];
               if( translatedTextToken.IsVariable )
               {
                  translatedTextPartial += "{{" + translatedTextToken.Character + "}}";
               }
               else
               {
                  translatedTextPartial += translatedTextToken.Character;
               }
            }

            translatedTextCursor = length;

            if( !HasTranslated( originalTextPartial, TranslationScopes.None, false ) )
            {
               AddTranslation( originalTextPartial, translatedTextPartial, TranslationScopes.None );
               _partialTranslations.Add( originalTextPartial );
            }
         }
      }

      private static List<TranslationCharacterToken> Tokenify( string text )
      {
         var result = new List<TranslationCharacterToken>( text.Length );

         for( int i = 0; i < text.Length; i++ )
         {
            var c = text[ i ];
            if( c == '{' && text.Length > i + 4 && text[ i + 1 ] == '{' && text[ i + 3 ] == '}' && text[ i + 4 ] == '}' )
            {
               c = text[ i + 2 ];
               result.Add( new TranslationCharacterToken( c, true ) );

               i += 4;
            }
            else
            {
               result.Add( new TranslationCharacterToken( c, false ) );
            }
         }

         return result;
      }

      private struct TranslationCharacterToken
      {
         public TranslationCharacterToken( char c, bool isVariable )
         {
            Character = c;
            IsVariable = isVariable;
         }

         public char Character { get; set; }

         public bool IsVariable { get; set; }
      }

      private void LoadTranslationsInFile( string fullFileName, bool isSubstitutionFile, bool isOutputFile )
      {
         var fileExists = File.Exists( fullFileName );
         if( fileExists || isSubstitutionFile )
         {
            if( fileExists )
            {
               XuaLogger.Current.Debug( $"Loading texts: {fullFileName}." );

               var context = new TranslationFileLoadingContext();

               string[] translations = File.ReadAllLines( fullFileName, Encoding.UTF8 );
               foreach( string translatioOrDirective in translations )
               {
                  if( Settings.EnableTranslationScoping && !isOutputFile )
                  {
                     var directive = TranslationFileDirective.Create( translatioOrDirective );
                     if( directive != null )
                     {
                        context.Apply( directive );

                        XuaLogger.Current.Debug( "Directive in file: " + fullFileName + ": " + directive.ToString() );
                        continue;
                     }
                  }

                  if( context.IsExecutable( Settings.ApplicationName ) )
                  {
                     string[] kvp = translatioOrDirective.Split( TranslationSplitters, StringSplitOptions.None );
                     if( kvp.Length == 2 )
                     {
                        string key = TextHelper.Decode( kvp[ 0 ] );
                        string value = TextHelper.Decode( kvp[ 1 ] );

                        if( !string.IsNullOrEmpty( key ) && !string.IsNullOrEmpty( value ) )
                        {
                           if( isSubstitutionFile )
                           {
                              if( key != null && value != null )
                              {
                                 Settings.Replacements[ key ] = value;
                              }
                           }
                           else
                           {

                              if( key.StartsWith( "r:" ) )
                              {
                                 try
                                 {
                                    var regex = new RegexTranslation( key, value );

                                    var levels = context.GetLevels();
                                    if( levels.Count == 0 )
                                    {
                                       AddTranslationRegex( regex, TranslationScopes.None );
                                    }
                                    else
                                    {
                                       foreach( var level in levels )
                                       {
                                          AddTranslationRegex( regex, level );
                                       }
                                    }
                                 }
                                 catch( Exception e )
                                 {
                                    XuaLogger.Current.Warn( e, $"An error occurred while constructing regex translation: '{translatioOrDirective}'." );
                                 }
                              }
                              else
                              {
                                 var levels = context.GetLevels();
                                 if( levels.Count == 0 )
                                 {
                                    AddTranslation( key, value, TranslationScopes.None );
                                 }
                                 else
                                 {
                                    foreach( var level in levels )
                                    {
                                       AddTranslation( key, value, level );
                                    }
                                 }
                              }
                           }
                        }
                     }
                  }
               }
            }
            else if( isSubstitutionFile )
            {
               using( var stream = File.Create( fullFileName ) )
               {
                  stream.Write( new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3 ); // UTF-8 BOM
                  stream.Close();
               }
            }
         }
      }

      private void LoadStaticTranslations()
      {
         if( Settings.UseStaticTranslations && Settings.FromLanguage == Settings.DefaultFromLanguage && Settings.Language == Settings.DefaultLanguage )
         {
            // load static translations from previous titles
            string[] translations = Properties.Resources.StaticTranslations.Split( new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries );
            foreach( string translation in translations )
            {
               string[] kvp = translation.Split( TranslationSplitters, StringSplitOptions.None );
               if( kvp.Length >= 2 )
               {
                  string key = TextHelper.Decode( kvp[ 0 ] );
                  string value = TextHelper.Decode( kvp[ 1 ] );

                  if( !string.IsNullOrEmpty( key ) && !string.IsNullOrEmpty( value ) )
                  {
                     _staticTranslations[ key ] = value;
                  }
               }
            }
         }
      }

      internal void SaveNewTranslationsToDisk()
      {
         if( _newTranslations.Count > 0 )
         {
            lock( _writeToFileSync )
            {
               if( _newTranslations.Count > 0 )
               {
                  using( var stream = File.Open( Settings.AutoTranslationsFilePath, FileMode.Append, FileAccess.Write ) )
                  using( var writer = new StreamWriter( stream, Encoding.UTF8 ) )
                  {
                     foreach( var kvp in _newTranslations )
                     {
                        writer.WriteLine( TextHelper.Encode( kvp.Key ) + '=' + TextHelper.Encode( kvp.Value ) );
                     }
                     writer.Flush();
                  }
                  _newTranslations.Clear();
               }
            }
         }
      }

      private void AddTranslationRegex( RegexTranslation regex, int scope )
      {
         if( !_registeredRegexes.Contains( regex.Original ) )
         {
            if( scope != TranslationScopes.None )
            {
               if( !_scopedTranslations.TryGetValue( scope, out var dicts ) )
               {
                  dicts = new TranslationDictionaries();
                  _scopedTranslations.Add( scope, dicts );
               }

               dicts.RegisteredRegexes.Add( regex.Original );
               dicts.DefaultRegexes.Add( regex );
            }
            else
            {
               _registeredRegexes.Add( regex.Original );
               _defaultRegexes.Add( regex );
            }
         }
      }

      private bool HasTranslated( string key, int scope, bool checkAll )
      {
         if( checkAll )
         {
            return _translations.ContainsKey( key )
               || ( scope != TranslationScopes.None && _scopedTranslations.TryGetValue( scope, out var td ) && td.Translations.ContainsKey( key ) );
         }
         else if( scope != TranslationScopes.None )
         {
            return scope != TranslationScopes.None && _scopedTranslations.TryGetValue( scope, out var td ) && td.Translations.ContainsKey( key );
         }
         else
         {
            return _translations.ContainsKey( key );
         }
      }

      private bool IsTranslation( string translation, int scope )
      {
         if( HasTranslated( translation, scope, true ) )
         {
            return false;
         }

         if( _reverseTranslations.ContainsKey( translation ) )
         {
            return true;
         }
         else if( scope != TranslationScopes.None && _scopedTranslations.TryGetValue( scope, out var td ) && td.ReverseTranslations.ContainsKey( translation ) )
         {
            return true;
         }

         return false;
      }

      private bool IsTokenTranslation( string translation, int scope )
      {
         if( _reverseTokenTranslations.ContainsKey( translation ) )
         {
            return true;
         }
         else if( scope != TranslationScopes.None && _scopedTranslations.TryGetValue( scope, out var td ) && td.ReverseTokenTranslations.ContainsKey( translation ) )
         {
            return true;
         }

         return false;
      }

      private void AddTranslation( string key, string value, int scope )
      {
         if( key != null && value != null )
         {
            if( scope != TranslationScopes.None )
            {
               if( !_scopedTranslations.TryGetValue( scope, out var dicts ) )
               {
                  dicts = new TranslationDictionaries();
                  _scopedTranslations.Add( scope, dicts );
               }

               dicts.Translations[ key ] = value;
               dicts.ReverseTranslations[ value ] = key;
            }
            else
            {
               _translations[ key ] = value;
               _reverseTranslations[ value ] = key;
            }
         }
      }

      private void AddTokenTranslation( string key, string value, int scope )
      {
         if( key != null && value != null )
         {
            if( scope != TranslationScopes.None )
            {
               if( !_scopedTranslations.TryGetValue( scope, out var tokenDicts ) )
               {
                  tokenDicts = new TranslationDictionaries();
                  _scopedTranslations.Add( scope, tokenDicts );
               }

               tokenDicts.TokenTranslations[ key ] = value;
               tokenDicts.ReverseTokenTranslations[ value ] = key;
            }
            else
            {
               _tokenTranslations[ key ] = value;
               _reverseTokenTranslations[ value ] = key;
            }
         }
      }

      private void QueueNewTranslationForDisk( string key, string value )
      {
         lock( _writeToFileSync )
         {
            _newTranslations[ key ] = value;
         }
      }

      internal void AddTranslationToCache( string key, string value, bool persistToDisk, TranslationType type, int scope )
      {
         if( ( type & TranslationType.Token ) == TranslationType.Token )
         {
            AddTokenTranslation( key, value, scope );
         }

         if( ( type & TranslationType.Full ) == TranslationType.Full )
         {
            if( !HasTranslated( key, scope, false ) )
            {
               AddTranslation( key, value, scope );

               // also add a modified version of the translation
               var ukey = new UntranslatedText( key, false, true, Settings.FromLanguageUsesWhitespaceBetweenWords );
               var uvalue = new UntranslatedText( value, false, true, Settings.ToLanguageUsesWhitespaceBetweenWords );
               if( ukey.Original_Text_ExternallyTrimmed != key && !HasTranslated( ukey.Original_Text_ExternallyTrimmed, scope, false ) )
               {
                  AddTranslation( ukey.Original_Text_ExternallyTrimmed, uvalue.Original_Text_ExternallyTrimmed, scope );
               }
               if( ukey.Original_Text_ExternallyTrimmed != ukey.Original_Text_FullyTrimmed && !HasTranslated( ukey.Original_Text_FullyTrimmed, scope, false ) )
               {
                  AddTranslation( ukey.Original_Text_FullyTrimmed, uvalue.Original_Text_FullyTrimmed, scope );
               }

               if( persistToDisk )
               {
                  if( scope != TranslationScopes.None )
                  {
                     XuaLogger.Current.Error( "Stored scoped translation to cache, even though this is not supported!" );
                  }

                  QueueNewTranslationForDisk( key, value );
               }
            }
         }
      }

      internal bool TryGetTranslation( UntranslatedText key, bool allowRegex, bool allowToken, int scope, out string value )
      {
         bool result;
         string untemplated;
         string unmodifiedValue;
         string unmodifiedKey;

         string untemplated_TemplatedOriginal_Text = null;
         string untemplated_TemplatedOriginal_Text_InternallyTrimmed = null;
         string untemplated_TemplatedOriginal_Text_ExternallyTrimmed = null;
         string untemplated_TemplatedOriginal_Text_FullyTrimmed = null;

         TranslationDictionaries dicts = null;
         if( scope != TranslationScopes.None && _scopedTranslations.TryGetValue( scope, out dicts ) )
         {
            // first lookup token, if we allow it! - ONLY ORIGINAL and INTERNALLY FIXED VARIATIONS
            if( allowToken && dicts.TokenTranslations.Count > 0 )
            {
               if( key.IsTemplated && !key.IsFromSpammingComponent )
               {
                  // key.TemplatedOriginal_Text = '   What are you \ndoing here, {{A}}?'
                  // untemplated                = '   What are you \ndoing here, Sophie?'

                  // FIRST: Check scoped translations
                  // lookup original
                  untemplated = untemplated_TemplatedOriginal_Text ?? ( untemplated_TemplatedOriginal_Text = key.Untemplate( key.TemplatedOriginal_Text ) );
                  result = dicts.TokenTranslations.TryGetValue( untemplated, out value );
                  if( result )
                  {
                     return result;
                  }

                  // lookup internally trimmed
                  if( !ReferenceEquals( key.TemplatedOriginal_Text, key.TemplatedOriginal_Text_InternallyTrimmed ) )
                  {
                     // key.TemplatedOriginal_Text_InternallyTrimmed = '   What are you doing here, {{A}}?'
                     // untemplated                                  = '   What are you doing here, Sophie?'

                     untemplated = untemplated_TemplatedOriginal_Text_InternallyTrimmed ?? ( untemplated_TemplatedOriginal_Text_InternallyTrimmed = key.Untemplate( key.TemplatedOriginal_Text_InternallyTrimmed ) );
                     result = dicts.TokenTranslations.TryGetValue( untemplated, out value );
                     if( result )
                     {
                        return result;
                     }
                  }
               }

               // lookup original
               // key.TemplatedOriginal_Text = '   What are you \ndoing here, {{A}}?'

               // FIRST: Check scoped translations
               // lookup original
               result = dicts.TokenTranslations.TryGetValue( key.TemplatedOriginal_Text, out value );
               if( result )
               {
                  return result;
               }

               // lookup internally trimmed
               if( !ReferenceEquals( key.TemplatedOriginal_Text, key.TemplatedOriginal_Text_InternallyTrimmed ) )
               {
                  // key.TemplatedOriginal_Text_InternallyTrimmed = '   What are you doing here, {{A}}?'

                  result = dicts.TokenTranslations.TryGetValue( key.TemplatedOriginal_Text_InternallyTrimmed, out value );
                  if( result )
                  {
                     return result;
                  }
               }
            }

            // lookup UNTEMPLATED translations - ALL VARIATIONS
            if( key.IsTemplated && !key.IsFromSpammingComponent )
            {
               // key.TemplatedOriginal_Text = '   What are you \ndoing here, {{A}}?'
               // untemplated                = '   What are you \ndoing here, Sophie?'

               // FIRST: Check scoped translations
               if( dicts.Translations.Count > 0 )
               {
                  // lookup original
                  untemplated = untemplated_TemplatedOriginal_Text ?? ( untemplated_TemplatedOriginal_Text = key.Untemplate( key.TemplatedOriginal_Text ) );
                  result = dicts.Translations.TryGetValue( untemplated, out value );
                  if( result )
                  {
                     return result;
                  }

                  // lookup original minus external whitespace
                  if( !ReferenceEquals( key.TemplatedOriginal_Text, key.TemplatedOriginal_Text_ExternallyTrimmed ) )
                  {
                     // key.TemplatedOriginal_Text_ExternallyTrimmed = 'What are you \ndoing here, {{A}}?'
                     // untemplated                                  = 'What are you \ndoing here, Sophie?'

                     untemplated = untemplated_TemplatedOriginal_Text_ExternallyTrimmed ?? ( untemplated_TemplatedOriginal_Text_ExternallyTrimmed = key.Untemplate( key.TemplatedOriginal_Text_ExternallyTrimmed ) );
                     result = dicts.Translations.TryGetValue( untemplated, out value );
                     if( result )
                     {
                        // WHITESPACE DIFFERENCE, Store new value
                        unmodifiedValue = key.LeadingWhitespace + value + key.TrailingWhitespace;
                        unmodifiedKey = untemplated_TemplatedOriginal_Text ?? ( untemplated_TemplatedOriginal_Text = key.Untemplate( key.TemplatedOriginal_Text ) );

                        if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Whitespace difference (c1): '{unmodifiedKey}' => '{unmodifiedValue}'" );
                        AddTranslationToCache( unmodifiedKey, unmodifiedValue, false, TranslationType.Full, scope );

                        value = unmodifiedValue;
                        return result;
                     }
                  }

                  // lookup internally trimmed
                  if( !ReferenceEquals( key.TemplatedOriginal_Text, key.TemplatedOriginal_Text_InternallyTrimmed ) )
                  {
                     // key.TemplatedOriginal_Text_InternallyTrimmed = '   What are you doing here, {{A}}?'
                     // untemplated                                  = '   What are you doing here, Sophie?'

                     untemplated = untemplated_TemplatedOriginal_Text_InternallyTrimmed ?? ( untemplated_TemplatedOriginal_Text_InternallyTrimmed = key.Untemplate( key.TemplatedOriginal_Text_InternallyTrimmed ) );
                     result = dicts.Translations.TryGetValue( untemplated, out value );
                     if( result )
                     {
                        // WHITESPACE DIFFERENCE, Store new value
                        unmodifiedValue = value;
                        unmodifiedKey = untemplated_TemplatedOriginal_Text ?? ( untemplated_TemplatedOriginal_Text = key.Untemplate( key.TemplatedOriginal_Text ) );

                        if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Whitespace difference (c2): '{unmodifiedKey}' => '{unmodifiedValue}'" );
                        AddTranslationToCache( unmodifiedKey, unmodifiedValue, false, TranslationType.Full, scope );

                        value = unmodifiedValue;
                        return result;
                     }
                  }

                  // lookup internally trimmed minus external whitespace
                  if( !ReferenceEquals( key.TemplatedOriginal_Text_InternallyTrimmed, key.TemplatedOriginal_Text_FullyTrimmed ) )
                  {
                     // key.TemplatedOriginal_Text_FullyTrimmed = 'What are you doing here, {{A}}?'
                     // untemplated                             = 'What are you doing here, Sophie?'

                     untemplated = untemplated_TemplatedOriginal_Text_FullyTrimmed ?? ( untemplated_TemplatedOriginal_Text_FullyTrimmed = key.Untemplate( key.TemplatedOriginal_Text_FullyTrimmed ) );
                     result = dicts.Translations.TryGetValue( untemplated, out value );
                     if( result )
                     {
                        // WHITESPACE DIFFERENCE, Store new value
                        unmodifiedValue = key.LeadingWhitespace + value + key.TrailingWhitespace;
                        unmodifiedKey = untemplated_TemplatedOriginal_Text ?? ( untemplated_TemplatedOriginal_Text = key.Untemplate( key.TemplatedOriginal_Text ) );

                        if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Whitespace difference (c3): '{unmodifiedKey}' => '{unmodifiedValue}'" );
                        AddTranslationToCache( unmodifiedKey, unmodifiedValue, false, TranslationType.Full, scope );

                        value = unmodifiedValue;
                        return result;
                     }
                  }
               }
            }

            // lookup original - ALL VARATIONS
            // FIRST: Check scoped translations
            if( dicts.Translations.Count > 0 )
            {
               // key.TemplatedOriginal_Text = '   What are you \ndoing here, {{A}}?'
               result = dicts.Translations.TryGetValue( key.TemplatedOriginal_Text, out value );
               if( result )
               {
                  return result;
               }

               // lookup original minus external whitespace
               if( !ReferenceEquals( key.TemplatedOriginal_Text, key.TemplatedOriginal_Text_ExternallyTrimmed ) )
               {
                  // key.TemplatedOriginal_Text_ExternallyTrimmed = 'What are you \ndoing here, {{A}}?'

                  result = dicts.Translations.TryGetValue( key.TemplatedOriginal_Text_ExternallyTrimmed, out value );
                  if( result )
                  {
                     // WHITESPACE DIFFERENCE, Store new value
                     unmodifiedValue = key.LeadingWhitespace + value + key.TrailingWhitespace;

                     if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Whitespace difference (c7): '{key.TemplatedOriginal_Text}' => '{unmodifiedValue}'" );
                     AddTranslationToCache( key.TemplatedOriginal_Text, unmodifiedValue, false, TranslationType.Full, scope );

                     value = unmodifiedValue;
                     return result;
                  }
               }

               // lookup internally trimmed
               if( !ReferenceEquals( key.TemplatedOriginal_Text, key.TemplatedOriginal_Text_InternallyTrimmed ) )
               {
                  // key.TemplatedOriginal_Text_InternallyTrimmed = '   What are you doing here, {{A}}?'

                  result = dicts.Translations.TryGetValue( key.TemplatedOriginal_Text_InternallyTrimmed, out value );
                  if( result )
                  {
                     // WHITESPACE DIFFERENCE, Store new value
                     if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Whitespace difference (c8): '{key.TemplatedOriginal_Text}' => '{value}'" );
                     AddTranslationToCache( key.TemplatedOriginal_Text, value, false, TranslationType.Full, scope ); // FIXED: using templated original

                     return result;
                  }
               }

               // lookup internally trimmed minus external whitespace
               if( !ReferenceEquals( key.TemplatedOriginal_Text_InternallyTrimmed, key.TemplatedOriginal_Text_FullyTrimmed ) )
               {
                  // key.TemplatedOriginal_Text_FullyTrimmed = 'What are you doing here, {{A}}?'

                  result = dicts.Translations.TryGetValue( key.TemplatedOriginal_Text_FullyTrimmed, out value );
                  if( result )
                  {
                     // WHITESPACE DIFFERENCE, Store new value
                     unmodifiedValue = key.LeadingWhitespace + value + key.TrailingWhitespace;

                     if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Whitespace difference (c9): '{key.TemplatedOriginal_Text}' => '{unmodifiedValue}'" );
                     AddTranslationToCache( key.TemplatedOriginal_Text, unmodifiedValue, false, TranslationType.Full, scope );

                     value = unmodifiedValue;
                     return result;
                  }
               }
            }
         }








         // first lookup token, if we allow it! - ONLY ORIGINAL and INTERNALLY FIXED VARIATIONS
         if( allowToken && _tokenTranslations.Count > 0 )
         {
            if( key.IsTemplated && !key.IsFromSpammingComponent )
            {
               // key.TemplatedOriginal_Text = '   What are you \ndoing here, {{A}}?'
               // untemplated                = '   What are you \ndoing here, Sophie?'

               // THEN: Check unscoped translations
               // lookup original
               untemplated = untemplated_TemplatedOriginal_Text ?? ( untemplated_TemplatedOriginal_Text = key.Untemplate( key.TemplatedOriginal_Text ) );
               result = _tokenTranslations.TryGetValue( untemplated, out value );
               if( result )
               {
                  return result;
               }

               // lookup internally trimmed
               if( !ReferenceEquals( key.TemplatedOriginal_Text, key.TemplatedOriginal_Text_InternallyTrimmed ) )
               {
                  // key.TemplatedOriginal_Text_InternallyTrimmed = '   What are you doing here, {{A}}?'
                  // untemplated                                  = '   What are you doing here, Sophie?'

                  untemplated = untemplated_TemplatedOriginal_Text_InternallyTrimmed ?? ( untemplated_TemplatedOriginal_Text_InternallyTrimmed = key.Untemplate( key.TemplatedOriginal_Text_InternallyTrimmed ) );
                  result = _tokenTranslations.TryGetValue( untemplated, out value );
                  if( result )
                  {
                     return result;
                  }
               }
            }

            // lookup original
            // key.TemplatedOriginal_Text = '   What are you \ndoing here, {{A}}?'

            // THEN: Check unscoped translations
            // lookup original
            result = _tokenTranslations.TryGetValue( key.TemplatedOriginal_Text, out value );
            if( result )
            {
               return result;
            }

            // lookup internally trimmed
            if( !ReferenceEquals( key.TemplatedOriginal_Text, key.TemplatedOriginal_Text_InternallyTrimmed ) )
            {
               // key.TemplatedOriginal_Text_InternallyTrimmed = '   What are you doing here, {{A}}?'

               result = _tokenTranslations.TryGetValue( key.TemplatedOriginal_Text_InternallyTrimmed, out value );
               if( result )
               {
                  return result;
               }
            }
         }

         // lookup UNTEMPLATED translations - ALL VARIATIONS
         if( key.IsTemplated && !key.IsFromSpammingComponent )
         {
            // key.TemplatedOriginal_Text = '   What are you \ndoing here, {{A}}?'
            // untemplated                = '   What are you \ndoing here, Sophie?'

            // THEN: Check unscoped translations
            // lookup original
            untemplated = untemplated_TemplatedOriginal_Text ?? ( untemplated_TemplatedOriginal_Text = key.Untemplate( key.TemplatedOriginal_Text ) );
            result = _translations.TryGetValue( untemplated, out value );
            if( result )
            {
               return result;
            }

            // lookup original minus external whitespace
            if( !ReferenceEquals( key.TemplatedOriginal_Text, key.TemplatedOriginal_Text_ExternallyTrimmed ) )
            {
               // key.TemplatedOriginal_Text_ExternallyTrimmed = 'What are you \ndoing here, {{A}}?'
               // untemplated                                  = 'What are you \ndoing here, Sophie?'

               untemplated = untemplated_TemplatedOriginal_Text_ExternallyTrimmed ?? ( untemplated_TemplatedOriginal_Text_ExternallyTrimmed = key.Untemplate( key.TemplatedOriginal_Text_ExternallyTrimmed ) );
               result = _translations.TryGetValue( untemplated, out value );
               if( result )
               {
                  // WHITESPACE DIFFERENCE, Store new value
                  unmodifiedValue = key.LeadingWhitespace + value + key.TrailingWhitespace;
                  unmodifiedKey = untemplated_TemplatedOriginal_Text ?? ( untemplated_TemplatedOriginal_Text = key.Untemplate( key.TemplatedOriginal_Text ) );

                  if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Whitespace difference (c4): '{unmodifiedKey}' => '{unmodifiedValue}'" );
                  AddTranslationToCache( unmodifiedKey, unmodifiedValue, Settings.CacheWhitespaceDifferences, TranslationType.Full, TranslationScopes.None );

                  value = unmodifiedValue;
                  return result;
               }
            }

            // lookup internally trimmed
            if( !ReferenceEquals( key.TemplatedOriginal_Text, key.TemplatedOriginal_Text_InternallyTrimmed ) )
            {
               // key.TemplatedOriginal_Text_InternallyTrimmed = '   What are you doing here, {{A}}?'
               // untemplated                                  = '   What are you doing here, Sophie?'

               untemplated = untemplated_TemplatedOriginal_Text_InternallyTrimmed ?? ( untemplated_TemplatedOriginal_Text_InternallyTrimmed = key.Untemplate( key.TemplatedOriginal_Text_InternallyTrimmed ) );
               result = _translations.TryGetValue( untemplated, out value );
               if( result )
               {
                  // WHITESPACE DIFFERENCE, Store new value
                  unmodifiedValue = value;
                  unmodifiedKey = untemplated_TemplatedOriginal_Text ?? ( untemplated_TemplatedOriginal_Text = key.Untemplate( key.TemplatedOriginal_Text ) );

                  if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Whitespace difference (c5): '{unmodifiedKey}' => '{unmodifiedValue}'" );
                  AddTranslationToCache( unmodifiedKey, unmodifiedValue, Settings.CacheWhitespaceDifferences, TranslationType.Full, TranslationScopes.None );

                  value = unmodifiedValue;
                  return result;
               }
            }

            // lookup internally trimmed minus external whitespace
            if( !ReferenceEquals( key.TemplatedOriginal_Text_InternallyTrimmed, key.TemplatedOriginal_Text_FullyTrimmed ) )
            {
               // key.TemplatedOriginal_Text_FullyTrimmed = 'What are you doing here, {{A}}?'
               // untemplated                             = 'What are you doing here, Sophie?'

               untemplated = untemplated_TemplatedOriginal_Text_FullyTrimmed ?? ( untemplated_TemplatedOriginal_Text_FullyTrimmed = key.Untemplate( key.TemplatedOriginal_Text_FullyTrimmed ) );
               result = _translations.TryGetValue( untemplated, out value );
               if( result )
               {
                  // WHITESPACE DIFFERENCE, Store new value
                  unmodifiedValue = key.LeadingWhitespace + value + key.TrailingWhitespace;
                  unmodifiedKey = untemplated_TemplatedOriginal_Text ?? ( untemplated_TemplatedOriginal_Text = key.Untemplate( key.TemplatedOriginal_Text ) );

                  if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Whitespace difference (c6): '{unmodifiedKey}' => '{unmodifiedValue}'" );
                  AddTranslationToCache( unmodifiedKey, unmodifiedValue, Settings.CacheWhitespaceDifferences, TranslationType.Full, TranslationScopes.None );

                  value = unmodifiedValue;
                  return result;
               }
            }
         }

         // THEN: Check unscoped translations
         // key.TemplatedOriginal_Text = '   What are you \ndoing here, {{A}}?'
         result = _translations.TryGetValue( key.TemplatedOriginal_Text, out value );
         if( result )
         {
            return result;
         }

         // lookup original minus external whitespace
         if( !ReferenceEquals( key.TemplatedOriginal_Text, key.TemplatedOriginal_Text_ExternallyTrimmed ) )
         {
            // key.TemplatedOriginal_Text_ExternallyTrimmed = 'What are you \ndoing here, {{A}}?'

            result = _translations.TryGetValue( key.TemplatedOriginal_Text_ExternallyTrimmed, out value );
            if( result )
            {
               // WHITESPACE DIFFERENCE, Store new value
               unmodifiedValue = key.LeadingWhitespace + value + key.TrailingWhitespace;

               if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Whitespace difference (c10): '{key.TemplatedOriginal_Text}' => '{unmodifiedValue}'" );
               AddTranslationToCache( key.TemplatedOriginal_Text, unmodifiedValue, Settings.CacheWhitespaceDifferences, TranslationType.Full, TranslationScopes.None );

               value = unmodifiedValue;
               return result;
            }
         }

         // lookup internally trimmed
         if( !ReferenceEquals( key.TemplatedOriginal_Text, key.TemplatedOriginal_Text_InternallyTrimmed ) )
         {
            // key.TemplatedOriginal_Text_InternallyTrimmed = '   What are you doing here, {{A}}?'

            result = _translations.TryGetValue( key.TemplatedOriginal_Text_InternallyTrimmed, out value );
            if( result )
            {
               // WHITESPACE DIFFERENCE, Store new value
               if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Whitespace difference (c11): '{key.TemplatedOriginal_Text}' => '{value}'" );
               AddTranslationToCache( key.TemplatedOriginal_Text, value, Settings.CacheWhitespaceDifferences, TranslationType.Full, TranslationScopes.None ); // FIXED: using templated original

               return result;
            }
         }

         // lookup internally trimmed minus external whitespace
         if( !ReferenceEquals( key.TemplatedOriginal_Text_InternallyTrimmed, key.TemplatedOriginal_Text_FullyTrimmed ) )
         {
            // key.TemplatedOriginal_Text_FullyTrimmed = 'What are you doing here, {{A}}?'

            result = _translations.TryGetValue( key.TemplatedOriginal_Text_FullyTrimmed, out value );
            if( result )
            {
               // WHITESPACE DIFFERENCE, Store new value
               unmodifiedValue = key.LeadingWhitespace + value + key.TrailingWhitespace;

               if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Whitespace difference (c12): '{key.TemplatedOriginal_Text}' => '{unmodifiedValue}'" );
               AddTranslationToCache( key.TemplatedOriginal_Text, unmodifiedValue, Settings.CacheWhitespaceDifferences, TranslationType.Full, TranslationScopes.None );

               value = unmodifiedValue;
               return result;
            }
         }

         // regex lookups - ONLY ORIGNAL VARIATION
         if( allowRegex )
         {
            if( dicts != null && dicts.DefaultRegexes.Count > 0 )
            {
               for( int i = dicts.DefaultRegexes.Count - 1; i > -1; i-- )
               {
                  var regex = dicts.DefaultRegexes[ i ];
                  try
                  {
                     var match = regex.CompiledRegex.Match( key.TemplatedOriginal_Text );
                     if( !match.Success ) continue;

                     value = regex.CompiledRegex.Replace( key.TemplatedOriginal_Text, regex.Translation );

                     if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Regex lookup: '{key.TemplatedOriginal_Text}' => '{value}'" );
                     AddTranslationToCache( key.TemplatedOriginal_Text, value, false, TranslationType.Full, scope );

                     return true;
                  }
                  catch( Exception e )
                  {
                     dicts.DefaultRegexes.RemoveAt( i );

                     XuaLogger.Current.Error( e, $"Failed while attempting to replace or match text of regex '{regex.Original}'. Removing that regex from the cache." );
                  }
               }
            }

            for( int i = _defaultRegexes.Count - 1; i > -1; i-- )
            {
               var regex = _defaultRegexes[ i ];
               try
               {
                  var match = regex.CompiledRegex.Match( key.TemplatedOriginal_Text );
                  if( !match.Success ) continue;

                  value = regex.CompiledRegex.Replace( key.TemplatedOriginal_Text, regex.Translation );

                  if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Regex lookup: '{key.TemplatedOriginal_Text}' => '{value}'" );
                  AddTranslationToCache( key.TemplatedOriginal_Text, value, Settings.CacheRegexLookups, TranslationType.Full, TranslationScopes.None );

                  return true;
               }
               catch( Exception e )
               {
                  _defaultRegexes.RemoveAt( i );

                  XuaLogger.Current.Error( e, $"Failed while attempting to replace or match text of regex '{regex.Original}'. Removing that regex from the cache." );
               }
            }
         }

         // static lookups - ALL VARIATIONS
         if( _staticTranslations.Count > 0 )
         {
            // lookup based on templated value alone, which may also be the original text
            // key.TemplatedOriginal_Text = '   What are you \ndoing here, {{A}}?'

            // lookup original
            result = _staticTranslations.TryGetValue( key.TemplatedOriginal_Text, out value );
            if( result )
            {
               if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Static lookup: '{key.TemplatedOriginal_Text}' => '{value}'" );
               AddTranslationToCache( key.TemplatedOriginal_Text, value, true, TranslationType.Full, TranslationScopes.None );

               return result;
            }

            // lookup internally trimmed
            if( !ReferenceEquals( key.TemplatedOriginal_Text, key.TemplatedOriginal_Text_InternallyTrimmed ) )
            {
               // key.TemplatedOriginal_Text_InternallyTrimmed = '   What are you doing here, {{A}}?'

               result = _staticTranslations.TryGetValue( key.TemplatedOriginal_Text_InternallyTrimmed, out value );
               if( result )
               {
                  if( !Settings.EnableSilentMode ) XuaLogger.Current.Info( $"Static lookup: '{key.TemplatedOriginal_Text_InternallyTrimmed}' => '{value}'" );
                  AddTranslationToCache( key.TemplatedOriginal_Text, value, true, TranslationType.Full, TranslationScopes.None );

                  return result;
               }
            }
         }

         return result;
      }

      internal bool TryGetReverseTranslation( string value, int scope, out string key )
      {
         return ( scope != TranslationScopes.None && _scopedTranslations.TryGetValue( scope, out var td ) && td.ReverseTranslations.TryGetValue( value, out key ) ) ||
            _reverseTranslations.TryGetValue( value, out key );
      }

      internal bool IsTranslatable( string text, bool isToken, int scope )
      {
         var translatable = LanguageHelper.IsTranslatable( text ) && !IsTranslation( text, scope );
         if( isToken && translatable )
         {
            translatable = !IsTokenTranslation( text, scope );
         }
         return translatable;
      }

      internal bool IsPartial( string text, int scope )
      {
         return _partialTranslations.Contains( text );
      }

      public class TranslationDictionaries
      {
         public TranslationDictionaries()
         {
            Translations = new Dictionary<string, string>();
            ReverseTranslations = new Dictionary<string, string>();
            DefaultRegexes = new List<RegexTranslation>();
            RegisteredRegexes = new HashSet<string>();
            TokenTranslations = new Dictionary<string, string>();
            ReverseTokenTranslations = new Dictionary<string, string>();
         }

         public Dictionary<string, string> TokenTranslations { get; }
         public Dictionary<string, string> ReverseTokenTranslations { get; }
         public Dictionary<string, string> Translations { get; }
         public Dictionary<string, string> ReverseTranslations { get; }
         public List<RegexTranslation> DefaultRegexes { get; }
         public HashSet<string> RegisteredRegexes { get; }
      }
   }
}