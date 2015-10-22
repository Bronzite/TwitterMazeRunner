using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Threading.Tasks;
using Tweetinvi;
using Tweetinvi.Core.Credentials;
using Tweetinvi.Core.Parameters;
using Tweetinvi.Core.Interfaces;

namespace TwitterMazeRunner
{
    class Program
    {
        static void Main(string[] args)
        {
            //Load credentials from out-project so we don't accidentally post them to GitHub.
            StreamReader srConfigFileReader = new StreamReader("g:\\TEMP\\twittermazerunner.config");
            string sConsumerKey = srConfigFileReader.ReadLine();
            string sConsumerSecret = srConfigFileReader.ReadLine();
            srConfigFileReader.Close();

            //Get a Twitter Credential
            TwitterCredentials twCredentials = new TwitterCredentials(sConsumerKey, sConsumerSecret);

            //Get Authorization URL
            string sAuthorizationURL = CredentialsCreator.GetAuthorizationURL(twCredentials);

            Console.Write("Input Twitter PIN Number:");

            //Initiate PIN Login Sequence
            System.Diagnostics.Process.Start(sAuthorizationURL);

            //Take in the PIN Number from the user.
            string sPinNumber = Console.ReadLine();

            try
            {
                //Use the PIN to get the User Token
                ITwitterCredentials itwUserCredentials = CredentialsCreator.GetCredentialsFromVerifierCode(sPinNumber, twCredentials);

                //Set the User Token to the global Auth object
                Auth.SetCredentials(itwUserCredentials);
            }
            catch(Exception ex)
            {
                while(ex!=null)
                {
                    Console.WriteLine("ERROR: {0}", ex.Message);
                    ex = ex.InnerException;
                }

                //Assume this error is fatal; just run the program again.
                return;
            }

            //Load Maze from XML
            XmlDocument xmlMazeFile = new XmlDocument();
            xmlMazeFile.Load("MazeDescription.xml");

            //Get all the maze nodes.
            XmlNodeList xmlMazes = xmlMazeFile.SelectNodes("//maze");
            
            //Randomly select one of the nodes to run.
            Random randMazeSelector = new Random();
            int iSelectedMaze = randMazeSelector.Next(xmlMazes.Count);
            Maze mazeCurrent = new Maze((XmlElement)xmlMazes[iSelectedMaze]);

         
            //Set current location to start node.
            int iCurrentLocation = mazeCurrent.StartNodeID;

            //Publish the starting tweet.
            PublishTweetParameters ptp = new PublishTweetParameters(string.Format ("Help me!  I woke up in {0}.  Where should I go?",mazeCurrent.Name ));
            Tweet.PublishTweet(ptp);

            //Initialize Timers.
            //Two because we might need to tally votes more often than we move.
            DateTime dtLastMove = DateTime.Now;
            DateTime dtLastVoteCheck = DateTime.Now;

            //We need to print the move number to avoid Twitter blocking repeat tweets.
            int iMoveNumber = 0;

            Dictionary<string, int> dictExitVotes = null;

            //Publish the room we start in.
            Room roomCurrent = mazeCurrent.Rooms[iCurrentLocation];
            TweetCurrentRoom(roomCurrent,iMoveNumber);
            dictExitVotes = new Dictionary<string, int>();
            foreach (string sExitName in roomCurrent.Exits.Keys)
                dictExitVotes.Add(sExitName, 0);

            //Main Loop -- continue until we reach the end node.
            while (iCurrentLocation != mazeCurrent.EndNodeID)
            {
                //Before we tally the votes, take the current time.
                //This avoids us losing the votes that come in during processing
                //They'll be picked up the next time through.
                DateTime dtPreTallyTimestamp = DateTime.Now;
                TallyNewVotes(dictExitVotes, dtLastVoteCheck);
                dtLastVoteCheck = dtPreTallyTimestamp;

                //Every two minutes, move to the exit that has the most votes.
                if(DateTime.Now - dtLastMove > TimeSpan.FromSeconds(120))
                {
                    int iHighVotes = 0;
                    string sPreferredExit = "";
                    foreach(string sExit in dictExitVotes.Keys)
                    {
                        if(dictExitVotes[sExit] > iHighVotes)
                        {
                            iHighVotes = dictExitVotes[sExit];
                            sPreferredExit = sExit;
                        }
                    }
                    if(sPreferredExit != "")
                    {
                        iMoveNumber++;
                        TweetMove(sPreferredExit,iMoveNumber);
                        iCurrentLocation = mazeCurrent.Rooms[iCurrentLocation].Exits[sPreferredExit];
                        roomCurrent = mazeCurrent.Rooms[iCurrentLocation];
                        TweetCurrentRoom(roomCurrent,iMoveNumber);
                        dictExitVotes = new Dictionary<string, int>();
                        foreach (string sExitName in roomCurrent.Exits.Keys)
                            dictExitVotes.Add(sExitName, 0);
                        dtLastMove = DateTime.Now;
                    }

                }
                System.Threading.Thread.Sleep(61000);
                //Check our rate limits.
                Tweetinvi.Core.Interfaces.Credentials.ITokenRateLimits rlAll = RateLimit.GetCurrentCredentialsRateLimits();
                //Sometime rlAll comes back Null.  Not sure why yet.
                if (rlAll != null)
                {
                    Tweetinvi.Core.Interfaces.Credentials.ITokenRateLimit rlMentionsTimeline = rlAll.StatusesMentionsTimelineLimit;
                    Tweetinvi.Core.Interfaces.Credentials.ITokenRateLimit rlTweets = rlAll.ApplicationRateLimitStatusLimit;
                    Console.WriteLine("Tweets Limit Remaining: {0}/{1} ({2} seconds to reset)", rlMentionsTimeline.Remaining, rlMentionsTimeline.Limit, rlMentionsTimeline.ResetDateTimeInSeconds);
                    Console.WriteLine("Mention Timeline Limit Remaining: {0}/{1} ({2} seconds to reset)", rlMentionsTimeline.Remaining, rlMentionsTimeline.Limit, rlMentionsTimeline.ResetDateTimeInSeconds);
                }
            }

            //Post a tweet announcing that we're shutting down.
            ptp = new PublishTweetParameters(string.Format("[{0}] My bed!  I finally get to sleep!  Thank you.  Zzzzzzz....",iMoveNumber));
            Tweet.PublishTweet(ptp);
        }

        /// <summary>
        /// Simply count up the votes in all the recent mentions.
        /// </summary>
        /// <param name="dictExitVotes"></param>
        /// <param name="dtThreshold"></param>
        static void TallyNewVotes(Dictionary<string,int> dictExitVotes, DateTime dtThreshold)
        {
            IEnumerable<IMention> ieMentions =  Timeline.GetMentionsTimeline(500);
            
            if (ieMentions != null)
            {
                List<IMention> lstMentions = new List<IMention>(ieMentions);
                foreach (IMention mentionCurrent in lstMentions)
                {
                    if (mentionCurrent.CreatedAt > dtThreshold)
                    {
                        List<string> lstKeys = new List<string>(dictExitVotes.Keys);
                        foreach (string sKey in lstKeys)
                        {
                            if (mentionCurrent.Text.ToLower().Contains(sKey.ToLower()))
                            {
                                dictExitVotes[sKey]++;
                            }
                        }
                    }
                }

                //Print the latest votes for debugging purposes.
                if(lstMentions.Count>0)
                foreach (string sKey in dictExitVotes.Keys)
                {
                    Console.WriteLine("{0}:{1}", sKey, dictExitVotes[sKey]);
                }
            }
        }

        /// <summary>
        /// Tweet when we move to a new location.
        /// </summary>
        /// <param name="sExit"></param>
        static void TweetMove(string sExit, int iMoveNumber)
        {
            string sTweetText = string.Format("[{1}] I'm moving {0}.",sExit,iMoveNumber);
            Console.WriteLine("Tweeting: {0}", sTweetText);
            PublishTweetParameters ptpRoomStatus = new PublishTweetParameters(sTweetText);

            Tweet.PublishTweet(ptpRoomStatus);
        }

        /// <summary>
        /// Tweet the description and exits from the current room.
        /// </summary>
        /// <param name="roomCurrent"></param>
        static void TweetCurrentRoom(Room roomCurrent, int iMoveNumber)
        {
            string[] sExitNames = new string[roomCurrent.Exits.Count];
            roomCurrent.Exits.Keys.CopyTo(sExitNames, 0);

            string sTweetText = "";
            if(sExitNames.Length > 1)
                sTweetText=  string.Format("[{2}]{0}\n\nExits are {1}.", roomCurrent.Description, JoinWithOxfordComma( sExitNames),iMoveNumber);
            else
                sTweetText = string.Format("[{2}]{0}\n\nThe only exit is {1}.", roomCurrent.Description,sExitNames[0], iMoveNumber);

            Console.WriteLine("Tweeting: {0}", sTweetText);

            PublishTweetParameters ptpRoomStatus = new PublishTweetParameters(sTweetText);

            Tweet.PublishTweet(ptpRoomStatus);
        }

        /// <summary>
        /// Join a series of elements together.  For example, join ['A','B','C'] together
        /// as 'A, B, and C'
        /// </summary>
        /// <param name="sElementsOfList"></param>
        /// <returns></returns>
        static string JoinWithOxfordComma(string[] sElementsOfList)
        {
            if (sElementsOfList.Length == 1) return sElementsOfList[0];
            if (sElementsOfList.Length == 2) return string.Format("{0} and {1}", sElementsOfList[0], sElementsOfList[1]);
            string retval = "";
            for (int i = 0;i<sElementsOfList.Length -1;i++)
            {
                retval = string.Format("{0}{1}, ", retval, sElementsOfList[i]);
            }
            retval = string.Format("{0}and {1}", retval, sElementsOfList[sElementsOfList.Length - 1]);
            return retval;
        }
    }

    
}
