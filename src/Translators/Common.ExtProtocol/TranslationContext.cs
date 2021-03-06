﻿using System;

namespace Common.ExtProtocol
{
   public class TranslationContext : ITranslationContext
   {
      public TranslationContext(
         string[] untranslatedTexts,
         string sourceLanguage,
         string destinationLanguage)
      {
         UntranslatedTexts = untranslatedTexts;
         SourceLanguage = sourceLanguage;
         DestinationLanguage = destinationLanguage;
      }

      public string UntranslatedText => UntranslatedTexts[ 0 ];
      public string[] UntranslatedTexts { get; }
      public string SourceLanguage { get; }
      public string DestinationLanguage { get; }

      internal bool IsDone { get; private set; }

      public string[] TranslatedTexts { get; private set; }
      public string ErrorMessage { get; private set; }
      public Exception Error { get; private set; }


      public void Complete( string translatedText )
      {
         Complete( new[] { translatedText } );
      }

      public void Complete( string[] translatedTexts )
      {
         if( IsDone ) return;

         try
         {
            if( translatedTexts.Length == 0 )
            {
               FailContext( "Received empty translation from translator.", null );
               return;
            }

            for( int i = 0; i < translatedTexts.Length; i++ )
            {
               var translatedText = translatedTexts[ 0 ];
               if( string.IsNullOrEmpty( translatedText ) )
               {
                  FailContext( "Received empty translation from translator.", null );
                  return;
               }
            }

            CompleteContext( translatedTexts );
         }
         finally
         {
            IsDone = true;
         }
      }

      public void Fail( string reason, Exception exception )
      {
         if( IsDone ) return;

         try
         {
            FailContext( reason, exception );

            throw new TranslationContextException();
         }
         finally
         {
            IsDone = true;
         }
      }

      public void Fail( string reason )
      {
         if( IsDone ) return;

         try
         {
            FailContext( reason, null );

            throw new TranslationContextException();
         }
         finally
         {
            IsDone = true;
         }
      }

      public void FailWithoutThrowing( string reason, Exception exception )
      {
         if( IsDone ) return;

         try
         {
            FailContext( reason, exception );
         }
         finally
         {
            IsDone = true;
         }
      }

      internal void FailIfNotCompleted()
      {
         if( !IsDone )
         {
            FailWithoutThrowing( "The translation request was not completed before returning from translator.", null );
         }
      }

      public void FailContext( string reason, Exception exception )
      {
         ErrorMessage = reason;
         Error = exception;
      }

      public void CompleteContext( string[] translatedTexts )
      {
         TranslatedTexts = translatedTexts;
      }
   }
}
